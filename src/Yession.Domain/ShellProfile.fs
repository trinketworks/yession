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
      WorkingDirectory : RootPath option }

module ShellProfile =

    /// No profile: the sandbox decides, as it always did.
    let none : ShellProfile = { WorkingDirectory = None }

/// Every sandbox's profile: the ones somebody SET, projected from the log, and the one the
/// repos' files DECLARE, held beside them. Per sandbox and not per session because a path
/// is only a path inside the filesystem that has it: the default sandbox's workspace, a
/// named sandbox's, and a docker sandbox's bind are three different trees, and one
/// session-wide string would be a fact in one of them and a broken shell in the rest.
///
/// Two layers, and only the first is an event. A set is somebody's decision, so it is
/// recorded, shown, and outlives the process. A declaration is a reading of the files as
/// they stand, recomputed on every fold, so writing it down only made a second copy that
/// could disagree with the files — and did, a fold comparing the two and rewriting the log
/// twelve times in two days (session 3V9P). The set wins wherever there is one.
type ShellProfileProjection =
    { Profiles : Map<SandboxRef, ShellProfile>
      Declared : Map<SandboxRef, RootPath> }

module ShellProfileProjection =

    let empty : ShellProfileProjection = { Profiles = Map.empty; Declared = Map.empty }

    /// Fold one event. The newest set wins, and a set carrying no directory REMOVES the
    /// entry rather than storing an empty profile — so "has a profile" and "starts
    /// somewhere" are the same question, which is the only way the query and the spawn can
    /// agree without either of them knowing about the other.
    ///
    /// A set authored by a repo's FILE is passed over. Those are what the fold used to
    /// write for `session.shell-profile`; the declared layer now reads the file itself, so
    /// replaying one would put back the copy it replaced.
    let applyEvent (proj: ShellProfileProjection) (event: SessionEvent) : ShellProfileProjection =
        match event with
        | ShellProfileSet p when (match p.Actor with Configured _ -> true | _ -> false) -> proj
        | ShellProfileSet p ->
            match p.WorkingDirectory with
            | Some _ -> { proj with Profiles = proj.Profiles |> Map.add p.Sandbox { WorkingDirectory = p.WorkingDirectory } }
            | None -> { proj with Profiles = proj.Profiles |> Map.remove p.Sandbox }
        | _ -> proj

    /// What the files declare for this sandbox now: a directory, or nothing.
    let declare (sandbox: SandboxRef) (path: RootPath option) (proj: ShellProfileProjection) : ShellProfileProjection =
        match path with
        | Some path -> { proj with Declared = proj.Declared |> Map.add sandbox path }
        | None -> { proj with Declared = proj.Declared |> Map.remove sandbox }

    /// Where a shell opened in this sandbox starts, if anywhere: the set one, else the
    /// declared one, else wherever the sandbox puts it. What a spawn asks, and the only
    /// thing it asks.
    let workingDirectory (sandbox: SandboxRef) (proj: ShellProfileProjection) : RootPath option =
        match proj.Profiles |> Map.tryFind sandbox with
        | Some profile -> profile.WorkingDirectory
        | None -> proj.Declared |> Map.tryFind sandbox

    /// The sandboxes that have one, set or declared, in name order — the query's rows.
    let listed (proj: ShellProfileProjection) : (SandboxRef * ShellProfile) list =
        Seq.append (Map.keys proj.Profiles) (Map.keys proj.Declared)
        |> Seq.distinct
        |> Seq.map (fun sandbox -> sandbox, { WorkingDirectory = workingDirectory sandbox proj })
        |> List.ofSeq
        |> List.sortBy (fst >> SandboxRef.render)

/// What the repos' files make of the `default` sandbox's profile (`session.shell-profile`).
///
/// COMPUTED from which repos declare one, never from the order they arrived in: exactly one
/// declaring repo applies, none or several apply nothing. A "first repo wins" rule would
/// make the outcome depend on history nobody chose; this one a test can state from its
/// inputs. Whether it is the profile in EFFECT is the projection's to say: a set wins.
module ConfiguredProfile =

    let private sameRepo (a: RepoRef) (b: RepoRef) = RepoRef.value a = RepoRef.value b

    /// `declared` is every repo whose file has the key, its directory already resolved
    /// from the default sandbox's root.
    let declared (declared: (RepoRef * RootPath) list) : RootPath option =
        match declared with
        | [ _, path ] -> Some path
        | _ -> None

    /// What each declaring repo is told when more than one declares: the others' names, and
    /// that none of them applies. Empty for one or none.
    let conflicts (declared: (RepoRef * RootPath) list) : (RepoRef * string) list =
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
