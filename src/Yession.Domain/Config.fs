namespace Yession.Domain.Sandboxes

open Yession.Domain

// What a repo asks a session for (`yession.yaml`, Plan 27).
//
// The whole file is SANDBOXES, and that is the design rather than a starting point. The
// constraint is a complete algebra: every key needs a defined answer for "two repos both
// said something", and a sandbox is the only scope where that answer is TOTAL — it is
// named, and the name is scoped to its repo (`SandboxScope`), so the union of two files is
// disjoint by construction. No precedence rule, nothing shadows anything.
//
// So the rule for every future key: a key belongs here only if its scope is a sandbox.
// Anything session-wide (approval gates, MCP servers, a dependency on another repo) has no
// honest tie-break between two repos that disagree, and stays the operator's.
//
// This file is the MODEL: what a declaration is. Reading one from the file is the
// Session's (`Yession.Session.ConfigFile`), because the Session is what reads a checkout.

/// One sandbox as a repo declares it. A serialization of `EnvironmentSpec` plus the three
/// things `start_work_sandbox` already takes, so nothing here is a new concept — the file
/// says what the commands could already be told.
type SandboxDecl =
    { /// What this sandbox runs IN, when the repo asked for a container at all.
      ///
      /// `None` is not "confined" — it is the repo saying nothing, and what that means is the
      /// BACKEND's answer, not this file's. Nesting the container's own keys under one
      /// optional block is what makes `cmd` unwritable on a sandbox that has no container to
      /// run it: there is no flat `cmd` to mistype.
      Container : ContainerSpec option
      /// Relative to THIS repo's checkout.
      WorkingDirectory : string option
      /// `SecretRef` values name a secret; they never carry one, and the type cannot.
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      /// The operator's resources this sandbox selects, by name.
      ///
      /// A repo can never write a host path or a hostname. It does not know this machine's
      /// layout, and the same file has to work on a laptop, in CI, and on a host that keeps
      /// its caches somewhere else — so it names what the operator declared, and the
      /// operator owns what those names come to.
      ///
      /// This replaced `net:` and `read:`, which were the opposite arrangement: a repo
      /// naming a hostname and a host path directly, bounded by an environment variable that
      /// was a ceiling AND an unconditional grant at once. A repo's `read:` could therefore
      /// never obtain anything, and an operator could not offer a path without forcing it on
      /// every sandbox.
      Uses : ResourceName list
      /// Resources selected IF the host offers them — `EnvironmentSpec.Wants` carries the
      /// posture: an optimisation the same file can name everywhere, warm where the
      /// operator made it so and silently absent where not. A misspelled want is
      /// therefore never caught, which is the cost of the posture; a thing the sandbox
      /// NEEDS goes in `uses`, where a missing name refuses.
      Wants : ResourceName list
      /// Files to write into the sandbox's own home before anything runs in it.
      ///
      /// The one thing here a repo may write freely that is not a name, and it is not an
      /// exception to the rule above — it is the rule's other side. A path, a hostname and
      /// an executable all reach out of the sandbox and so must be the operator's to offer.
      /// A file in a home this session made for this sandbox reaches nothing, so there is
      /// nothing for an operator to bound and no reason to make somebody else write it.
      ///
      /// `HomePath` is what keeps that true: it cannot be absolute and cannot contain `..`,
      /// so nothing declared here lands outside the home.
      Files : Map<HomePath, string>
      /// One command to run in this sandbox before anything else does — the repo's chance
      /// to make the environment ready rather than describe it and hope.
      ///
      /// NOT `container.cmd`, which is the container's own process and carries a service's
      /// semantics: a `cmd` that exits takes the sandbox down with it, so setup written
      /// there would end the thing it was preparing. This runs INSIDE a sandbox that is
      /// already up, as a recorded block like any other, and finishing is what it is for.
      ///
      /// Idempotence is the repo's business, for the reason it is everywhere else here: a
      /// sandbox is restarted by things this file cannot see, and a setup that only works
      /// once is a sandbox that only works once.
      Setup : string option
      /// What this sandbox is FOR, in the repo's own words — the one thing here that is not
      /// a name, a path or a command, and is not addressed to the session at all.
      ///
      /// A name cannot carry it. This repository declares `dev` and `gate`, identical on
      /// every field a machine reads and different only in what a person meant; asked to run
      /// tests and shown both names, an agent picked `gate`, which is the one that holds a
      /// terminal for minutes. The repo knows why it declared two and had nowhere to say so.
      ///
      /// It reaches a reader wherever a sandbox is named — the start note, the queries — so
      /// whoever is choosing between them is choosing on the reason rather than the spelling.
      Description : string option
      /// Where this sandbox wants the session's checkouts to appear, absolute; absent takes
      /// the backend's own default (`/repos` in a container).
      ///
      /// A TARGET and never a source, which is what makes it the repo's to write at all — the
      /// same line `files:` draws. The checkouts arrive regardless; this only says where to
      /// look for them, inside a container this file already specifies entirely.
      Repos : string option }

module SandboxDecl =

    let empty : SandboxDecl =
        { Container = None
          WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Uses = []
          Wants = []
          Files = Map.empty
          Setup = None
          Description = None
          Repos = None }

    /// One declaration, written back as the file would have written it.
    ///
    /// Everything a set of declarations selects, each list deduplicated — the ONE assembly
    /// of "what these sandboxes ask for", however many declarations a repo carries. It was
    /// hand-collected at three sites, which is the missing-abstraction smell: a third
    /// selection posture beside `uses`/`wants` would have had to find every copy, and the
    /// copy it missed would have silently asked for less. Now a new posture changes this
    /// function and the type it returns, and the compiler walks to the consumers.
    let selectionOf (decls: SandboxDecl list) : ResourceName list * ResourceName list =
        decls |> List.collect (fun decl -> decl.Uses) |> List.distinct,
        decls |> List.collect (fun decl -> decl.Wants) |> List.distinct

    /// What a declaration ASKS the session for, given where this repo's checkout is.
    ///
    /// A rename rather than a translation, and that is the design: the file is a
    /// serialization of a vocabulary `start_work_sandbox` already spoke, so there is no
    /// second policy engine here and nothing this can express that a command could not.
    ///
    /// The checkout is the one thing a file cannot know — a path in it is relative to a
    /// directory the SESSION chose — so it is supplied, in BOTH its views (`CheckoutViews`):
    /// `workdir` resolves against the sandbox's own view, because it is where the sandbox
    /// starts; a `build:` context resolves against the host's, because the daemon client
    /// reads it from this filesystem before the container exists. Each is already
    /// guaranteed inside the checkout by the decoder, which is where a path a person can
    /// fix is refused; resolving is all that is left.
    ///
    /// `None` is a sandbox NOBODY'S repo declared: the session's own, which has no checkout
    /// for a relative path to be relative to. A `workdir` there is refused rather than
    /// resolved against something invented, and the refusal says which verb does move where
    /// a session sandbox's terminals start; a `build:` there is refused for the same reason.
    ///
    /// `Container = None` becomes `Confinement`, and that is not the file saying "confine
    /// me". It is the file saying nothing, and what nothing means is the BACKEND's answer:
    /// docker starts its defaults, srt and host confine. `Sandboxes.forBackend` is where the
    /// two authors meet.
    let toRequest (checkout: CheckoutViews option) (decl: SandboxDecl) : Result<SandboxRequest, string> =
        // Resolved rather than concatenated, and CLAMPED at the checkout. The decoder
        // already refuses a path that climbs out — that is the upstream fix, made where a
        // person can correct their file — and this is the downstream guard: a declaration
        // reaching here some other way still cannot name a directory above the checkout,
        // because there is no arithmetic here that could produce one.
        let under (checkout: string) (dir: string) =
            let resolved =
                dir.Split ([| '/'; '\\' |])
                |> Array.fold
                    (fun acc segment ->
                        match segment with
                        | "" | "." -> acc
                        | ".." -> (match acc with [] -> [] | _ :: rest -> rest)
                        | segment -> segment :: acc)
                    []
                |> List.rev
            match resolved with
            | [] -> checkout.TrimEnd '/'
            | segments -> checkout.TrimEnd '/' + "/" + String.concat "/" segments
        let workingDirectory =
            match decl.WorkingDirectory, checkout with
            | None, _ -> Ok None
            | Some dir, Some views -> Ok (Some (under views.InSandbox dir))
            | Some dir, None ->
                Error (
                    sprintf
                        "'%s' is relative to a checkout, and this sandbox is the session's own rather than a repo's                          — set_shell_profile moves where its terminals start"
                        dir)
        // The context leaves here HOST-absolute — the address the daemon client will
        // actually read — under the same clamp as `workdir`: nothing this arithmetic can
        // produce sits above the checkout, whatever reached it.
        let runtime =
            match decl.Container with
            | None -> Ok Confinement
            | Some container ->
                match container.Build, checkout with
                | None, _ -> Ok (Container container)
                | Some build, Some views ->
                    Ok (
                        Container
                            { container with
                                Build = Some { build with ContextPath = under views.OnHost build.ContextPath } })
                | Some build, None ->
                    Error (
                        sprintf
                            "a build context ('%s') is relative to a checkout, and this sandbox is the session's own rather than a repo's"
                            build.ContextPath)
        match workingDirectory, runtime with
        | Error e, _ -> Error e
        | _, Error e -> Error e
        | Ok workingDirectory, Ok runtime ->
            Ok
                { Spec =
                    { WorkingDirectory = workingDirectory
                      EnvironmentVariables = decl.EnvironmentVariables
                      Uses = decl.Uses
                      Wants = decl.Wants
                      Files = decl.Files
                      Runtime = runtime
                      Setup = decl.Setup
                      ReposAt = decl.Repos } }

/// One repo's whole file.
type ConfigFile =
    { /// Refused if it is not a version this build speaks. A file from the future says so
      /// rather than losing half its meaning to a decoder that skips what it cannot read.
      Version : int
      Sandboxes : Map<SandboxName, SandboxDecl> }
