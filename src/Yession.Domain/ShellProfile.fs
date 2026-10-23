namespace Yession.Domain.Sandboxes

open Yession.Domain

/// The shell profile (Plan 25): one durable fact about a sandbox's terminals — where a
/// shell opened in it starts. Set by the agent, read by everyone, folded from the event
/// log like every other projection, so a restarted session opens its next terminal where
/// the last one started.
///
/// STRUCTURE, deliberately, rather than rc text a shell would source. A free-text profile
/// would be an ungated command running in every future terminal, authored once and never
/// seen again — around the classifier, around the queue people read, and out of the
/// transcript, since a shell that starts by running somebody's script has already run it
/// before the first prompt mark. `execute_command` is the one door; each field here is
/// applied by the SPAWN, never typed at a prompt.
///
/// The next field this record grows is environment variables, and they must be
/// `EnvironmentVariableRef`s rather than strings: a profile is an EVENT, so a plain value is
/// recorded verbatim in the log and readable by everyone in the session for as long as it
/// exists. A token belongs in `set_secret` and a `SecretRef` to it belongs here, resolved at
/// open through the same seam the sandbox policy uses — and the query prints a ref by name,
/// never by value.
///
/// This is a layer over a LIVE sandbox, which is why it can be set mid-turn.
/// `EnvironmentSpec.WorkingDirectory` is fixed when the sandbox is created, so moving it means
/// recreating the sandbox — which kills everything running inside it. A default about where
/// new shells start is not worth somebody's build.

type ShellProfile =
    { /// Where a shell opened under this profile starts. `None` = the sandbox's own
      /// default, which is what every terminal did before this plan.
      WorkingDirectory : string option }

module ShellProfile =

    /// No profile: the sandbox decides, as it always did.
    let none : ShellProfile = { WorkingDirectory = None }

    /// Is `cwd` the directory `tree`, or somewhere inside it? What a caller deleting a tree
    /// asks about every profile it might have invalidated (Plan 26).
    ///
    /// A prefix on a DIRECTORY BOUNDARY, never a bare `StartsWith`: `/repos/hello-world`
    /// starts with `/repos/hello` and is a different checkout, so the naive test would clear
    /// a profile that is still perfectly good. Trailing slashes are trimmed on both sides
    /// because a path that names a directory may or may not carry one, and which it is says
    /// nothing about what it means.
    let isInside (tree: string) (cwd: string) : bool =
        let trimmed (path: string) = path.TrimEnd '/'
        let tree = trimmed tree
        let cwd = trimmed cwd
        tree <> "" && (cwd = tree || cwd.StartsWith (tree + "/"))

/// Every sandbox's profile, projected from the log. Per sandbox and not per session
/// because a path is only a path inside the filesystem that has it: the default sandbox's
/// workspace, a named sandbox's, and a docker sandbox's bind are three different trees,
/// and one session-wide string would be a fact in one of them and a broken shell in the
/// rest.
type ShellProfileProjection =
    { Profiles : Map<SandboxRef, ShellProfile> }

module ShellProfileProjection =

    let empty : ShellProfileProjection = { Profiles = Map.empty }

    /// Fold one event. The newest set wins, and a set carrying no directory REMOVES the
    /// entry rather than storing an empty profile — so "has a profile" and "starts
    /// somewhere" are the same question, which is the only way the query and the spawn can
    /// agree without either of them knowing about the other.
    let applyEvent (proj: ShellProfileProjection) (event: SessionEvent) : ShellProfileProjection =
        match event with
        | ShellProfileSet p ->
            match p.WorkingDirectory with
            | Some _ -> { Profiles = proj.Profiles |> Map.add p.Sandbox { WorkingDirectory = p.WorkingDirectory } }
            | None -> { Profiles = proj.Profiles |> Map.remove p.Sandbox }
        | _ -> proj

    let tryFind (sandbox: SandboxRef) (proj: ShellProfileProjection) : ShellProfile option =
        proj.Profiles |> Map.tryFind sandbox

    /// Where a shell opened in this sandbox starts, if anywhere — what a spawn asks, and
    /// the only thing it asks.
    let workingDirectory (sandbox: SandboxRef) (proj: ShellProfileProjection) : string option =
        proj |> tryFind sandbox |> Option.bind (fun profile -> profile.WorkingDirectory)

    /// The sandboxes that have one, in name order — the query's rows.
    let listed (proj: ShellProfileProjection) : (SandboxRef * ShellProfile) list =
        proj.Profiles |> Map.toList |> List.sortBy (fst >> SandboxRef.render)

/// What the repos' files make of the `default` sandbox's profile (`session.shell-profile`).
///
/// COMPUTED from which repos declare one, never from the order they arrived in: exactly one
/// declaring repo applies, none or several apply nothing, and a profile a person (or the
/// agent for one) set is theirs and no file moves it. A "first repo wins" rule would make
/// the outcome depend on history nobody chose; this one a test can state from its inputs.
[<RequireQualifiedAccess>]
type ConfiguredProfile =
    /// The profile already is what the files make of it, or a person chose it.
    | Leave
    /// Point it at this directory, as this repo's file.
    | Apply of RepoRef * string
    /// Clear the profile this repo's file set: no single file declares one any more.
    | Withdraw of RepoRef

module ConfiguredProfile =

    let private sameRepo (a: RepoRef) (b: RepoRef) = RepoRef.value a = RepoRef.value b

    /// `declared` is every repo whose file has the key, its directory already resolved to
    /// where the default sandbox's terminals reach it. `last` is the newest
    /// `ShellProfileSet` for that sandbox.
    let decide (declared: (RepoRef * string) list) (last: ShellProfileSet option) : ConfiguredProfile =
        let configured =
            last
            |> Option.bind (fun set ->
                match set.Actor with
                | Configured repo -> Some (repo, set.WorkingDirectory)
                | _ -> None)
        match last, configured with
        | Some _, None -> ConfiguredProfile.Leave
        | _ ->
            match declared, configured with
            | [ repo, path ], Some (setBy, Some current) when
                sameRepo repo setBy && current.TrimEnd '/' = path.TrimEnd '/' -> ConfiguredProfile.Leave
            | [ repo, path ], _ -> ConfiguredProfile.Apply (repo, path)
            | _, Some (setBy, Some _) -> ConfiguredProfile.Withdraw setBy
            | _ -> ConfiguredProfile.Leave

    /// What each declaring repo is told when more than one declares: the others' names, and
    /// that none of them applies. Empty for one or none.
    let conflicts (declared: (RepoRef * string) list) : (RepoRef * string) list =
        match declared with
        | [] | [ _ ] -> []
        | _ ->
            declared
            |> List.map (fun (repo, _) ->
                let others =
                    declared
                    |> List.map fst
                    |> List.filter (fun other -> not (sameRepo other repo))
                    |> List.map RepoRef.value
                    |> List.sort
                repo,
                sprintf
                    "%s also declare session.shell-profile. None of them applies. New terminals in default start where the sandbox puts them. set_shell_profile chooses"
                    (String.concat ", " others))
