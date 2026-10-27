module Yession.Host.Repos

// The session's repo manager (Plan 14, resurfaced by Plan 15): the agent's MCP verbs
// land here, every mutation appends an attributed event, and the conversation timeline
// is the record everyone reads.
//
// Plan 14 gave this a second, symmetric interface — a panel that could drive every verb.
// Plan 15 retired that half: a human who wants a repo added asks the agent, so there is
// one authorization path, one set of inputs to validate, and one place the act is
// recorded. What the panel is left with is the LISTING, which is now the `repos` query
// at the foot of this file. The cost was weighed and taken: with no working agent, nobody
// can add a repo at all. That is acceptable because it is the same session in which nothing
// else works either — and a panel button kept "just in case" would restore the second
// authorization path, the second set of inputs to validate, and the second place a
// mutation's record can go missing.
//
// Git itself runs through the sandbox seam under the AGENT backend (`host` is the
// explicitly lax choice; `srt` confines each spawn to the repos directory and the
// allowlisted egress). CLONE is the one exception, and it is a whole sandbox rather
// than a flag on a spawn: it writes a work tree whose names are the repo's, and srt
// refuses some of those names anywhere on the disk. See `unconfined` below.
// On top of the confinement, repo-controlled EXECUTION is disabled
// per invocation — hooks, fsmonitor, ext transport — because the WorkSandbox can write
// the repos directory by design, so a poisoned `.git/config` is assumed and made inert
// rather than trusted-by-placement. The same reasoning turns off the rest of what a flag or
// an env variable can turn off: symlinks in the work tree (`core.symlinks=false`), diff
// drivers (`--no-ext-diff --no-textconv`), and under `host` any proxy the config names
// (`NO_PROXY=*`). A checkout whose `.git` is not a directory of its own is not a checkout
// (`present`), and a verb in a checkout has its repository and work tree pinned in its env
// (`checkoutPins`), so `core.worktree` cannot move it. One vector is left, because nothing
// switches it off: a `filter.<driver>.smudge`
// runs on `switch_branch`, with the git sandbox's reach (docs/GAPS.md).
//
// The credential (the acting human's GitHub token, Plan 08 precedence) reaches no git
// process. Clone and fetch go through the git gateway, the same route a work sandbox's git
// takes (`GitGateway`): one `insteadOf` sends `https://github.com/` there, and a loan in the
// spawn's env names whose credential the gateway adds. The loan is lent before the spawn and
// returned after the exit. The token used to ride in the git process as an `extraheader`,
// where a `.git/config` proxy or `insteadOf` could send it anywhere (docs/technical-design.md
// §5, rule 21).

open System
open System.Collections.Generic
open Fable.Core
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Domain.Repos
open Yession.Session

// --- pure pieces (cheap-tier tested) -----------------------------------------------------

/// A branch name the switch verb will accept: a conservative subset of what
/// `git check-ref-format` allows, enough for every real branch and none of the
/// option-injection shapes (`-`-leading) or traversal shapes (`..`).
let validBranchName (raw: string) : Result<string, string> =
    let charOk c = Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' || c = '/'
    match Option.ofObj raw |> Option.map (fun r -> r.Trim ()) with
    | None | Some "" -> Error "branch name cannot be empty"
    | Some name ->
        if name.StartsWith "-" || name.StartsWith "." || name.StartsWith "/" then
            Error (sprintf "'%s' is not a valid branch name" name)
        elif name.EndsWith "/" || name.EndsWith ".lock" then
            Error (sprintf "'%s' is not a valid branch name" name)
        elif name.Contains ".." || name.Contains "//" || name.Contains "@{" then
            Error (sprintf "'%s' is not a valid branch name" name)
        elif name |> Seq.forall charOk then Ok name
        else Error (sprintf "'%s' is not a valid branch name" name)

/// The environment one git invocation runs with. Everything here is the point:
/// no global/system config (a repo's own `.git/config` is already untrusted — the
/// WorkSandbox can write it), no prompts, a pinned protocol allowlist, no credential
/// helpers, and the config-driven execution vectors (`hooksPath`, `fsmonitor`,
/// `protocol.ext`) forced off via `GIT_CONFIG_*` — which apply with the highest
/// precedence git knows, so a planted repo config cannot override them.
///
/// `credential.helper` is cleared explicitly because nulling the config FILES does not
/// reach it: Apple's git reads its own extra config (CommandLineTools
/// `share/git-core/gitconfig`, which sets `credential.helper=osxkeychain`) regardless of
/// `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_SYSTEM`, and `GIT_TERMINAL_PROMPT=0` does not cover
/// helpers. A helper here is never useful — the gateway adds the credential — and under
/// launchd the keychain helper blocks forever on a prompt nobody can answer, so a 401
/// became a clone hung for days. Cleared, the same fault fails in under a second.
///
/// It takes no credential. No git process holds one (docs/technical-design.md §5, rule 21):
/// the network verbs reach github.com through the git gateway, on a loan their spawn holds
/// (`GitRoute`), and this env sets no `http.*` key at all.
///
/// `core.symlinks=false` checks a committed symlink out as a plain file that holds the link
/// text. A checkout is read by processes that follow links, and a link a repository names can
/// point anywhere on the host, so a link in the work tree is a path the repository chose
/// outside it. The cost is that a repository which commits symlinks has plain files in its
/// checkout (docs/GAPS.md).
///
/// Under the `host` backend `NO_PROXY=*` disables any proxy a `.git/config` names: git hands
/// it to curl as `CURLOPT_NOPROXY`, and `http.<url>.proxy` matches by the longest URL, so a
/// proxy of our own could never outrank a planted one. Under srt the proxy IS the way out of
/// the sandbox (srt's own `HTTP_PROXY`), and its network namespace already makes any other
/// address unreachable, so the variable is left unset there.
let hardenedEnv (backend: SandboxBackend) (allowProtocol: string) : (string * string) list =
    let configs =
        [ "core.hooksPath", "/dev/null"
          "core.fsmonitor", "false"
          "protocol.ext.allow", "never"
          "credential.helper", ""
          "core.symlinks", "false" ]
    [ "GIT_CONFIG_GLOBAL", "/dev/null"
      "GIT_CONFIG_SYSTEM", "/dev/null"
      "GIT_TERMINAL_PROMPT", "0"
      "GIT_ALLOW_PROTOCOL", allowProtocol
      "GIT_CONFIG_COUNT", string (List.length configs) ]
    @ (match backend with
       | HostBackend -> [ "NO_PROXY", "*" ]
       | SrtBackend | DockerBackend -> [])
    @ (configs
       |> List.mapi (fun i (key, value) ->
           [ sprintf "GIT_CONFIG_KEY_%d" i, key
             sprintf "GIT_CONFIG_VALUE_%d" i, value ])
       |> List.concat)

/// Who git says made a commit, in git's own env spelling — author and committer both,
/// because a sandbox's git has no config of its own to fall back on and "Author identity
/// unknown" is the sentence a person pushing from one otherwise meets first.
let identityEnv (name: string) (email: string) : Map<string, string> =
    Map.ofList
        [ "GIT_AUTHOR_NAME", name
          "GIT_AUTHOR_EMAIL", email
          "GIT_COMMITTER_NAME", name
          "GIT_COMMITTER_EMAIL", email ]

/// The four `identityEnv` sets — what a block takes away when nobody's are known, so an
/// identity a shell was holding cannot outlive the act it was lent for.
let identityNames : string list =
    identityEnv "" "" |> Map.toList |> List.map fst

/// How long a credential with no identity behind it is not asked about again.
let identityMissWindow : TimeSpan = TimeSpan.FromSeconds 30.0

/// Who each credential commits as, asked of `lookup` once and kept for the session — a
/// profile does not change under one. A MISS is kept too, for `missFor`: a block's line
/// waits on this answer before it is typed, so a miss asked afresh for every block put
/// every block of a person with nothing connected — or with a profile github would not
/// give that minute — behind a round trip, and a github having a bad hour made their
/// blocks slow rather than nameless. Kept for a window rather than for good so a person
/// who connects github after their first block gets their name on the first block after
/// it, not after a restart.
let identityMemo
    (keyOf: 'credential -> string)
    (now: unit -> DateTimeOffset)
    (missFor: TimeSpan)
    (lookup: 'credential -> Async<Map<string, string> option>)
    : 'credential -> Async<Map<string, string> option> =
    let known = Collections.Generic.Dictionary<string, Map<string, string>> ()
    let missed = Collections.Generic.Dictionary<string, DateTimeOffset> ()
    fun credential ->
        async {
            let key = keyOf credential
            match known.TryGetValue key with
            | true, identity -> return Some identity
            | _ ->
                match missed.TryGetValue key with
                | true, at when now () - at < missFor -> return None
                | _ ->
                    match! lookup credential with
                    | Some identity ->
                        known.[key] <- identity
                        missed.Remove key |> ignore
                        return Some identity
                    | None ->
                        missed.[key] <- now ()
                        return None
        }

/// The git a verb runs, NAMED rather than looked up on PATH. Every other binary a
/// confined spawn execs is named for this reason — srt's bwrap, socat and ripgrep, the
/// agent's claude — and git was the exception until the exception cost a session.
///
/// On macOS `git` on PATH is `/usr/bin/git`, which is not git: it is a shim that resolves
/// a developer directory first, through `xcode-select` reading the `/var/select` symlink.
/// A sandbox that scopes its reads denies that symlink (srt's macOS escape hatch allows
/// metadata on DIRECTORIES, and a symlink is not one), so the shim fails before git runs
/// and reports it as a broken Xcode install — which is not what happened and not what
/// fixes it. The same shim is why `credential.helper` has to be cleared above.
///
/// Empty or unset keeps PATH, so nothing off-Nix regresses; the installable names one.
let gitExecutable (ambient: Map<string, string>) : string =
    ambient
    |> Map.tryFind "YESSION_BIN_GIT"
    |> Option.map (fun path -> path.Trim ())
    |> Option.filter (fun path -> path <> "")
    |> Option.defaultValue "git"

/// Which repository and which work tree a verb in a checkout acts on, pinned in its env.
///
/// `core.worktree` in `.git/config` moves a verb's work tree to any directory, and a
/// `.git/commondir` file moves where its objects, refs and config are read. A work sandbox can
/// write both. `GIT_DIR`, `GIT_WORK_TREE` and `GIT_COMMON_DIR` outrank them, so a verb in a
/// checkout acts on that checkout and nothing else. The clone names no checkout (`-C`) and gets
/// none: it creates the repository.
let checkoutPins (args: string list) : (string * string) list =
    match args with
    | "-C" :: checkout :: _ ->
        let gitDir = sprintf "%s/.git" checkout
        [ "GIT_DIR", gitDir; "GIT_WORK_TREE", checkout; "GIT_COMMON_DIR", gitDir ]
    | _ -> []

/// The one exec any git invocation here is built from — the probe and every verb alike.
///
/// A function rather than a record literal per call site, because `hardenedEnv` is not
/// hardening if a call site can be written without it, and one was: the probe spawned
/// `git --version` with an EMPTY env, which is an env no verb ever runs with. So it
/// answered a question about a git nothing here executes, and it answered it wrong.
///
/// git resolves its global config path before it does anything at all, `--version`
/// included. It tolerates an EACCES there — unreadable reads as absent — and treats every
/// other errno as fatal. A sandbox that denies the operator's home (Plan 24's read scope)
/// answers EPERM, not EACCES, so the probe died with `fatal: unable to access
/// '~/.config/git/config'` (exit 128) on a host where every verb, which nulls that path,
/// ran fine. The sandbox was then refused for its whole lifetime in words naming
/// `YESSION_BIN_GIT` and a resource: a working binary, and a read scope
/// whose only fault was doing its job. Following that advice would have widened the scope
/// to hand back the home it exists to deny.
let gitExec
    (git: string)
    (backend: SandboxBackend)
    (workingDirectory: string)
    (allowProtocol: string)
    (route: (string * string) list)
    (args: string list)
    : SandboxExec =
    { Executable = git
      Arguments = args
      Env = hardenedEnv backend allowProtocol @ checkoutPins args |> Map.ofList |> Sandboxes.withGitConfig route
      WorkingDirectory = Some workingDirectory
      // Housekeeping: git by path needs no toolchain and must not pay for one.
      Via = Direct }

/// What a sandbox that cannot run git says. It is a whole sentence with the two knobs in
/// it because the alternative is what shipped: the host binary's own parting words —
/// `xcode-select: error: unable to read data link ...` — which name neither the sandbox
/// nor anything an operator can set.
///
/// Both knobs are honest only because the probe now runs the verbs' env: what it rules out
/// before it speaks is every config file git would otherwise have gone looking for, so
/// what is left really is the binary or the runtime it reads.
let unusableGit (git: string) (reason: string) : string =
    sprintf
        "git ('%s') cannot run inside the sandbox: %s. Name a working git with YESSION_BIN_GIT. If that git needs files that the sandbox's read scope denies, declare those files as a resource in this host's profile (YESSION_SESSION_RESOURCES). Then put that resource in `default`."
        git
        reason

/// The subcommand an argument list runs — what a span is named for. `-C <path>` is how every
/// verb here addresses its checkout, so it is skipped rather than reported as the verb.
let gitVerb (args: string list) : string =
    match args with
    | "-C" :: _ :: verb :: _ -> verb
    | verb :: _ -> verb
    | [] -> "?"

/// What a git run that exited non-zero says: the subcommand, the checkout it ran in, and
/// git's own words.
///
/// The checkout is in the sentence because git's words often name none. It used to say
/// `git -C failed (exit 128): fatal: not a git repository: (null)` — `-C` as the verb and no
/// directory anywhere — and an agent reading it re-added a checkout that was fine, because
/// the one that was broken was a sibling it could not see named.
let gitFailure (args: string list) (code: int) (said: string) : string =
    match args with
    | "-C" :: checkout :: _ -> sprintf "git %s in %s failed (exit %d): %s" (gitVerb args) checkout code said
    | _ -> sprintf "git %s failed (exit %d): %s" (gitVerb args) code said

/// Cap rendered git output. The head is kept — a status/log/diff front-loads its
/// signal — and the elision is stated, never silent.
let capText (limit: int) (text: string) : string =
    if text.Length <= limit then text
    else sprintf "%s\n[%d more characters omitted]" (text.Substring (0, limit)) (text.Length - limit)

/// The most characters of a repo's root `AGENTS.md` a session reads: `agentsMdOf` truncates
/// past it, so what a maintainer writes beyond it never reaches the agent working in that
/// repo. A value of its own, rather than a local of `create`, so that the suite can hold THIS
/// repository's `AGENTS.md` to the cap its sessions will apply to it (`RepoLayout`) without
/// restating the number.
let agentsMdCharCap = 20_000

// --- the service -------------------------------------------------------------------------

type private GitRun =
    { Code : int
      Stdout : string
      Stderr : string }

/// How the session's own network verbs reach github.com: through the git gateway, which adds
/// the credential on the way (`GitGateway`). The same route a work sandbox's git is given, so
/// one mechanism carries a credential to github.com.
type GitRoute =
    { /// The git config that sends `https://github.com/` to the gateway: one `insteadOf`.
      Config : (string * string) list
      /// Lend `Lender`'s credential to ONE git process: the git config that carries the loan,
      /// and what returns it. Taken before the spawn and returned after the exit, so a loan
      /// lives exactly as long as the verb that spends it.
      Lend : GitGateway.Lender -> (string * string) list * (unit -> unit) }

module GitRoute =
    /// No gateway: the verbs reach whatever their URL names, with no credential. The test
    /// harness's `file://` fixtures, whose verbs never ask a remote over HTTP.
    let none : GitRoute = { Config = []; Lend = fun _ -> [], ignore }

    /// The route through `gateway`, at `host` as the git sandbox reaches this process
    /// (`Sandboxes.hostAddressHere`). One cap for the session's own git, and a borrower per
    /// verb: two verbs can run at once, and neither is a terminal.
    let through (gateway: GitGateway.Gateway) (host: string) : GitRoute =
        { Config = GitGateway.gitConfig host gateway.Port (gateway.Grant GitGateway.Routed.SessionGit)
          Lend =
            fun lender ->
                let borrower = GitGateway.Borrower.Verb (Guid.NewGuid ())
                let secret = gateway.Lend GitGateway.Routed.SessionGit borrower lender
                let key, value = GitGateway.loanConfig host gateway.Port secret
                [ key, value ], (fun () -> gateway.Retire borrower) }

type ReposConfig =
    { Backend : SandboxBackend
      /// The session's Docker network, for a backend that runs containers.
      Network : Sandboxes.DockerSandbox.Network
      /// The checkouts' one directory, the same path in every sandbox (`Sandboxes.checkoutAt`).
      /// Every listing carries a checkout's path under it, because the only reason to clone
      /// a repo is to work in it and the work happens in a terminal.
      ReposDir : string
      /// Where a terminal in the DEFAULT sandbox starts, NOW — an absolute path, the
      /// workspace or wherever `set_shell_profile` moved it. Every path a listing says is
      /// said from here (`SandboxPath.reachedFrom`), so it is one a terminal and a file
      /// tool there take as written. A fixed `repos/…` was true until a profile moved the
      /// start into the checkout, after which every file tool read it as
      /// `repos/…/repos/…` and answered "no such file" (session NR5KB8B5).
      StartsAt : unit -> string option
      /// Paths beyond `ReposDir` the git sandbox may READ. Empty in production; the
      /// test harness names its local bare-repo fixtures here. None of them may be an
      /// ANCESTOR of `ReposDir`: when both sit under a read-denied region (a HOME, which
      /// is where a session's data dir usually lives) srt re-binds each read path after
      /// the write binds, so an ancestor lands on top of the repos dir read-only and
      /// every clone fails. A sibling cannot cover it.
      ExtraReadPaths : string list
      /// The git binary every verb spawns (`gitExecutable`). A path, or `git` for the
      /// PATH lookup that is only safe where PATH's git is really git.
      Git : string
      /// Egress for the git sandbox. Production: the gateway's route, and nothing else.
      AllowedDomains : string list
      /// `GIT_ALLOW_PROTOCOL`. Production pins `http`, the gateway's; the test harness allows
      /// `file`.
      AllowProtocol : string
      /// How clone and fetch reach github.com (`GitRoute`).
      Route : GitRoute
      /// Repo -> clone URL. Production is `RepoRef.cloneUrl` (constructed, github.com
      /// only); the test harness points at local bare fixtures.
      CloneUrl : RepoRef -> string
      /// The GitHub token for the CREDENTIAL actor a caller names (Plan 08 precedence, applied
      /// by the composition), resolved by the gateway on each request a verb's loan carries,
      /// and by the provider's name check (`Canonical`). Never handed to git. None =
      /// anonymous — the gateway says so in words git prints.
      ResolveToken : CredentialFor -> Async<string option>
      /// What the provider calls this repo NOW, on this credential — `None` when it cannot
      /// say (unreachable, rate-limited, or a repo this credential cannot see, which the
      /// clone will say in its own words). A clone follows a renamed repo's old name with a
      /// redirect and keeps an `origin` the provider no longer answers to, so a name the
      /// provider has moved on from is refused before it is cloned, with the current one.
      Canonical : string option -> RepoRef -> Async<RepoRef option>
      /// github.com refused the credential resolved for this actor: the gateway was answered
      /// 401 on a verb's loan (`Lender.Refused`).
      ///
      /// Beside `ResolveToken` deliberately, because they are two halves of one story: a
      /// caller that can hand a verb a credential but cannot be told the verb failed with it
      /// is exactly the caller that leaves a dead credential reading as healthy. Nothing here
      /// decides what the refusal MEANS, so the composition asks the provider, which is the
      /// only place that knows.
      OnNetworkFailure : CredentialFor -> string -> Async<unit>
      /// Where the add path's spans go. Each step of a clone is its own span, because
      /// "adding a repo is slow" has six candidates and only a measurement picks one.
      Tracer : Telemetry.Tracer
      Log : EventLog<SessionEvent> }

/// Who is calling a mutating/network verb. The two halves genuinely differ for the
/// agent: the AGENT is the acting party the event records, while the CREDENTIAL is the
/// turn human's (Plan 08 — no borrowing across actors, and an agent has no scope of
/// its own). At the panel the two are the same person.
[<RequireQualifiedAccess>]
type RepoCaller =
    { Actor : ActorRef
      Credential : CredentialFor }

/// A verb's answer, and the item it recorded — `None` when it recorded nothing, as a
/// repeated add does. What a later act can point back to as its cause.
[<RequireQualifiedAccess>]
type Recorded<'a> =
    { Answer : 'a
      RecordedAs : MessageId option }

/// The Process-side repo manager. Caller-taking members append the acting party onto
/// the event; the read-only inspectors take none because they record nothing.
type ReposService =
    { AddRepo : RepoCaller -> RepoRef -> Async<Result<Recorded<RepoListing>, string>>
      /// Every checkout, each read on its own: one git cannot read is a row with its reason,
      /// never the listing's failure (`RepoRead`). `Error` is left for a listing that could
      /// not be made at all.
      ListRepos : unit -> Async<Result<RepoRead list, string>>
      SwitchBranch : RepoCaller -> RepoRef -> string -> bool -> Async<Result<Recorded<RepoListing>, string>>
      FetchRepo : RepoCaller -> RepoRef -> Async<Result<string, string>>
      RepoStatus : RepoRef -> Async<Result<string, string>>
      RepoLog : RepoRef -> Async<Result<string, string>>
      RepoDiff : RepoRef -> Async<Result<string, string>>
      /// Delete a checkout, and answer with the path it was at AS A TERMINAL SAW IT (Plan
      /// 26). That path rather than `unit`, because it is the one fact only this service has
      /// and the only one a shell profile could have been holding — the git sandbox's own
      /// path is visible to no terminal in this session.
      ///
      /// `force` is required to delete a checkout with uncommitted changes. An UNREADABLE
      /// checkout needs none: clearing one is what this verb was advertised for long before
      /// it existed, and refusing there would leave no way out of an interrupted clone.
      RemoveRepo : RepoCaller -> RepoRef -> bool -> Async<Result<string, string>> }

let private outputLimit = 20000

/// Where a clone is BUILT, under the repos directory, before it is anything.
///
/// `git clone` creates its target's `.git` within milliseconds and leaves it without a
/// HEAD for the rest of the clone, so a checkout at its visible path was never evidence
/// that there IS a checkout. Every reader in that window — a second `add_repo` taking the
/// already-here branch, the `repos` query, `repo_status` — met a repository git then
/// refused to describe: `fatal: ambiguous argument 'HEAD'`, which reads as "this repo is
/// empty" and is not. Building here and renaming into place makes the visible path binary:
/// absent, or whole.
///
/// The `~` is load-bearing. `RepoRef.create` admits only letters, digits, `-`, `_` and `.`
/// in an owner segment, so this name can never parse as one — the listing scan cannot
/// mistake a clone in progress for a repo BY CONSTRUCTION, rather than by every future
/// reader of that scan remembering to skip it. `Repos` pins that in the cheap tier.
let stagingDirName = "staging~"

let create (config: ReposConfig) : Result<ReposService, string> =
    Sandboxes.forBackend config.Backend config.Network "git" EnvironmentSpec.defaults
    |> Result.map (fun createSandbox ->

        /// The repos directory as an ABSOLUTE path, resolved once here and used for every
        /// path below — never `config.ReposDir` again.
        ///
        /// Every verb but the clone runs `git -C (pathOf repo)` in a sandbox whose working
        /// directory IS this directory, so a relative one is resolved twice: git looks for
        /// `<reposDir>/<reposDir>/owner/repo` and exits 128 with `cannot change to ...: No
        /// such file or directory`. The clone is the one verb that survives it, because it
        /// passes a target relative to that same cwd on purpose — so the checkout lands and
        /// then every verb, `add_repo`'s own listing included, reports a failure about it.
        /// A session whose data directory is relative (`Launch.unlaunched`'s
        /// default is) had exactly that: a repo on disk that no verb would admit to.
        ///
        /// Resolved HERE rather than only at the composition root because this is where a
        /// relative path stops being a path and starts being a wrong answer, and the next
        /// caller to build one of these has not read the root.
        let reposDir = Fs.absolute config.ReposDir

        let policy : SandboxPolicy =
            { ReadPaths = reposDir :: config.ExtraReadPaths
              WritePaths = [ reposDir ]
              AllowedDomains = Some config.AllowedDomains
              // git reaches remotes over the proxy, never a local socket.
              Sockets = []
              Binds = []
              Volumes = []
              Realisation = []
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = Some reposDir
              Filesystem = Confined
              Derived = Map.empty }

        /// Every git this service spawns, built in the one place that carries the hardened
        /// env (`gitExec`) — so the probe below cannot drift from the verbs it speaks for.
        let execFor (route: (string * string) list) (args: string list) : SandboxExec =
            gitExec config.Git config.Backend reposDir config.AllowProtocol route args

        /// `git --version` inside the sandbox, before any verb runs one. A sandbox that
        /// cannot run git is not a git sandbox, and this is where it says so: the
        /// alternative is every verb reporting whatever the host's binary printed on its
        /// way out, once per verb, in words that name neither the sandbox nor a knob.
        ///
        /// It runs a VERB's environment (no route: `--version` reaches no remote), because
        /// a probe that runs anything else proves nothing about the invocations it gates —
        /// which is exactly how it once refused a git that worked.
        ///
        /// It costs one spawn per sandbox lifetime — two per session, milliseconds under
        /// srt — and it is the guard that catches the NEXT unreadable runtime rather than
        /// only the one that prompted it.
        let usable (sandbox: Sandbox) : Async<Result<Sandbox, string>> =
            async {
                let mutable output = ""
                match! sandbox.Spawn (execFor [] [ "--version" ]) (fun (_, chunk) -> output <- output + chunk) with
                | Error reason -> return Error (unusableGit config.Git reason)
                | Ok handle ->
                    match! handle.Exited with
                    | SandboxExited 0 -> return Ok sandbox
                    | SandboxExited code ->
                        return
                            Error (unusableGit config.Git (sprintf "exit %d: %s" code (capText 400 (output.Trim ()))))
                    | SandboxSignalled signal -> return Error (unusableGit config.Git (sprintf "it was ended by %s" signal))
                    | SandboxKilledForMemory kill -> return Error (unusableGit config.Git (sprintf "it was %s" (MemoryKill.described kill)))
                    | SandboxRunFailed reason -> return Error (unusableGit config.Git reason)
            }

        // A sandbox per policy, created and PROVED on first use — under srt that is an
        // argv-rewriting wrapper, not a container, so per-verb spawns stay cheap. The
        // answer is remembered either way: a sandbox that failed its probe fails every
        // verb with the same sentence rather than re-probing per call.
        let lazySandbox (kind: string) (policy: SandboxPolicy) : unit -> Async<Result<Sandbox, string>> =
            let mutable ready : Result<Sandbox, string> option = None
            let named = [ "yession.git.sandbox", Telemetry.AttributeValue.String kind ]
            fun () ->
                async {
                    match ready with
                    | Some answer -> return answer
                    | None ->
                        let! answer =
                            async {
                                match!
                                    Telemetry.spanResult config.Tracer "git.sandbox.create" named (createSandbox policy)
                                with
                                | Error e -> return Error (sprintf "git sandbox: %s" e)
                                | Ok created -> return! Telemetry.spanResult config.Tracer "git.probe" named (usable created)
                            }
                        ready <- Some answer
                        return answer
                }

        let confined = lazySandbox "confined" policy

        /// The clone's sandbox, and nothing else's: a checkout can contain a `.vscode`,
        /// a `.mcp.json`, a `.gitmodules` — names srt refuses to write anywhere, over any
        /// allow-path — so the one verb that materializes a work tree runs with the
        /// filesystem unenforced. Egress stays pinned to `AllowedDomains`, the env stays
        /// the hardened one, and every other verb keeps the policy above: none of them
        /// writes a path srt objects to. Undo the moment srt can exempt a subtree instead
        /// of a whole spawn — `clone` goes back to `confined` and this sandbox goes away.
        let unconfined = lazySandbox "unconfined" { policy with Filesystem = Unconfined }

        /// One git process. A network verb names the `lender` whose credential it spends: the
        /// spawn then gets the gateway's route and a loan, lent before the spawn and returned
        /// after the exit, whatever the exit was.
        let runGit
            (sandboxFor: unit -> Async<Result<Sandbox, string>>)
            (lender: GitGateway.Lender option)
            (args: string list)
            : Async<Result<GitRun, string>> =
            async {
                match! sandboxFor () with
                | Error e -> return Error e
                | Ok sandbox ->
                    let loan, giveBack =
                        match lender with
                        | None -> [], ignore
                        | Some lender ->
                            let loan, giveBack = config.Route.Lend lender
                            config.Route.Config @ loan, giveBack
                    try
                        let mutable stdout = ""
                        let mutable stderr = ""
                        match! sandbox.Spawn (execFor loan args) (fun (stream, chunk) ->
                                  match stream with
                                  | Stdout -> stdout <- stdout + chunk
                                  | Stderr -> stderr <- stderr + chunk) with
                        | Error e -> return Error e
                        | Ok handle ->
                            match! handle.Exited with
                            | SandboxRunFailed reason -> return Error reason
                            | SandboxSignalled signal -> return Error (sprintf "git was ended by %s" signal)
                            | SandboxKilledForMemory kill -> return Error (sprintf "git was %s" (MemoryKill.described kill))
                            | SandboxExited code -> return Ok { Code = code; Stdout = stdout; Stderr = stderr }
                    finally
                        giveBack ()
            }

        /// Run and demand exit 0; a failure surfaces git's own words, capped. One span per
        /// run, named for the subcommand (`git clone`, `git status`) — the sandbox's own
        /// first-use cost lands inside it as a child, so a slow first verb says why.
        let runOk
            (sandboxFor: unit -> Async<Result<Sandbox, string>>)
            (lender: GitGateway.Lender option)
            (args: string list)
            : Async<Result<GitRun, string>> =
            let verb = gitVerb args
            Telemetry.spanResult
                config.Tracer
                (sprintf "git %s" verb)
                [ "yession.git.verb", Telemetry.AttributeValue.String verb
                  "yession.git.authenticated", Telemetry.AttributeValue.Bool lender.IsSome ]
                (async {
                    match! runGit sandboxFor lender args with
                    | Error e -> return Error e
                    | Ok run when run.Code <> 0 ->
                        let said = if run.Stderr.Trim () <> "" then run.Stderr else run.Stdout
                        return Error (gitFailure args run.Code (capText 2000 (said.Trim ())))
                    | Ok run -> return Ok run
                })

        let pathOf (repo: RepoRef) = sprintf "%s/%s" reposDir (RepoRef.relativePath repo)
        /// The same checkout as a TERMINAL in this session reaches it. What the listing
        /// reports, and the only path anything outside the git sandbox can act on.
        let visiblePathOf (repo: RepoRef) =
            SandboxPath.reachedFrom (config.StartsAt ()) (pathOf repo)
        /// A checkout is here when its owner directory, its own directory and its `.git` are
        /// each a DIRECTORY, by `lstat`. A work sandbox can write the repos directory, so any of
        /// the three can be a symlink to another repository on the host, and every verb would
        /// then run on that one: under `host`, `switch_branch` would check its files out where
        /// the work sandbox reads them. A link or a file at any of the three is "not here".
        let present (repo: RepoRef) =
            [ sprintf "%s/%s" reposDir (RepoRef.owner repo)
              pathOf repo
              sprintf "%s/.git" (pathOf repo) ]
            |> List.forall Fs.isOwnDirectory

        /// The checkout's own root `AGENTS.md`, read once at the moment the clone lands
        /// (repo AGENTS.md into per-turn context). Missing or unreadable is `None`, never a
        /// reason to fail an otherwise-successful clone -- an optional file's absence is not
        /// an error. Capped in characters, the same shape as Claude Code's own CLAUDE.md
        /// size ceiling, so one repo cannot dominate every turn's context; a file over the
        /// cap is truncated, not dropped, so a maintainer's opening lines still land.
        let agentsMdOf (repo: RepoRef) : string option =
            let path = sprintf "%s/AGENTS.md" (pathOf repo)
            if not (Fs.exists path) then None
            else
                try
                    let text = Fs.readText path
                    if text.Length > agentsMdCharCap then
                        Some (text.Substring (0, agentsMdCharCap) + "\n\n[truncated -- AGENTS.md is longer than this session reads]")
                    else
                        Some text
                with _ -> None

        /// Every skill a checkout's `.agents/skills/` declares, read at the same moment and
        /// for the same reason `agentsMdOf` is: right after the staging rename, the one
        /// moment a repo-authored file is trustworthy. A directory with no `SKILL.md`, one
        /// with no frontmatter, or frontmatter that does not decode contributes nothing --
        /// malformed is not a reason to fail an otherwise-successful clone, the same
        /// tolerance `agentsMdOf` already has. Sorted by name, so the menu reads the same on
        /// every clone whatever order the filesystem happens to answer in.
        let skillsOf (repo: RepoRef) : SkillSummary list =
            let skillsDir = sprintf "%s/.agents/skills" (pathOf repo)
            if not (Fs.exists skillsDir) then []
            else
                Fable.NodeExtras.Files.entries skillsDir
                |> Array.filter (fun entry -> entry.isDirectory ())
                |> Array.choose (fun entry ->
                    let path = sprintf "%s/%s/SKILL.md" skillsDir entry.name
                    if not (Fs.exists path) then None
                    else
                        try
                            SkillFile.frontmatterOf (Fs.readText path)
                            |> Option.bind (fun yaml -> SkillFile.decode (Fable.Yaml.Plain.json (Fable.Yaml.parse yaml)) |> Result.toOption)
                        with _ -> None)
                |> Array.sortBy (fun skill -> skill.Name)
                |> Array.toList

        /// Why a path in the repos directory is not a checkout, when `present` says it is not.
        let notACheckout (repo: RepoRef) : string =
            sprintf
                "%s is not a checkout: its .git, or a directory above it, is not a directory of its own. Use remove_repo on it, then add it again."
                (visiblePathOf repo)

        let listingOf (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                if not (present repo) then return Error (notACheckout repo)
                else
                    match! runOk confined None [ "-C"; pathOf repo; "rev-parse"; "--abbrev-ref"; "HEAD" ] with
                    | Error e -> return Error e
                    | Ok branch ->
                        match! runOk confined None [ "-C"; pathOf repo; "status"; "--porcelain" ] with
                        | Error e -> return Error e
                        | Ok status ->
                            return
                                Ok
                                    { Repo = repo
                                      Branch = branch.Stdout.Trim ()
                                      Dirty = status.Stdout.Trim () <> ""
                                      Path = visiblePathOf repo }
            }

        let mintMessageId () : MessageId =
            match MessageId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "message id invariant violated: %s" e

        let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
            async {
                let! _ = config.Log.Append actor event
                return ()
            }

        /// Which repos this session has: the LOG's answer, folded once and then kept by every
        /// append. The repos directory used to be the answer, and it answers a different
        /// question — what directories hold a `.git`. A worktree the agent made beside a
        /// checkout holds one too, and once a checkout had one path in every sandbox, the
        /// listing read it as a repo of its own and the fold read its `yession.yaml` as
        /// declaring sandboxes nobody added. Each row's STATE is still git's (`listingOf`).
        ///
        /// Heard before the replay lands is skipped: the replay already holds it, because
        /// the log tells listeners only once an append is readable.
        let mutable recorded : (ReposProjection * EventOffset option) option = None
        let _stopHearing =
            config.Log.OnAppended (fun appended ->
                match recorded with
                | Some (proj, through) when through |> Option.forall (fun o -> EventOffset.value appended.Offset > EventOffset.value o) ->
                    recorded <- Some (ReposProjection.applyEvent proj appended.Event, Some appended.Offset)
                | _ -> ())
        let recordedRepos () : Async<RepoRef list> =
            async {
                match recorded with
                | Some (proj, _) -> return proj.Repos |> List.map (fun r -> r.Repo)
                | None ->
                    let! page = config.Log.Read None Int32.MaxValue
                    // A second caller may have finished its replay while this one read; the
                    // first to land is kept, and appends since have been applied to it.
                    if recorded.IsNone then
                        let proj = page.Events |> List.fold (fun p e -> ReposProjection.applyEvent p e.Event) ReposProjection.empty
                        recorded <- Some (proj, page.Events |> List.tryLast |> Option.map (fun e -> e.Offset))
                    return recorded |> Option.map (fun (proj, _) -> proj.Repos |> List.map (fun r -> r.Repo)) |> Option.defaultValue []
            }

        let requirePresent (repo: RepoRef) (inner: unit -> Async<Result<'a, string>>) : Async<Result<'a, string>> =
            async {
                if not (present repo) then
                    return Error (sprintf "repo %s is not in this session. Use add_repo first." (RepoRef.value repo))
                else return! inner ()
            }

        /// Whose credential a network verb spends, as the gateway asks for it: the caller's
        /// CREDENTIAL half, resolved on every request the verb's loan carries.
        let lenderFor (caller: RepoCaller) : GitGateway.Lender =
            { Owner = caller.Credential
              Resolve = fun () -> config.ResolveToken caller.Credential
              Refused = fun () -> config.OnNetworkFailure caller.Credential "the git gateway was answered 401"
              // The session's own verbs clone and fetch; none of them pushes.
              Spent = fun _ -> async { return () } }

        /// Clone into the staging area and MOVE the finished thing into place. The rename
        /// is the moment the repo exists; before it, nothing at the visible path suggests
        /// one is coming, and a clone that dies leaves the wreckage somewhere nobody reads.
        let cloneInPlace (caller: RepoCaller) (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                let! token = Telemetry.span config.Tracer "repo.resolve_token" [] (config.ResolveToken caller.Credential)
                match! Telemetry.span config.Tracer "repo.canonical" [] (config.Canonical token repo) with
                | Some current when current <> repo ->
                    return
                        Error (
                            sprintf
                                "github now calls %s %s. Add %s instead. A checkout made under the old name clones through a redirect. It keeps an origin that the provider no longer answers to by that name."
                                (RepoRef.value repo)
                                (RepoRef.value current)
                                (RepoRef.value current))
                | _ ->
                // Relative, because the sandbox's working directory is the repos dir; git
                // creates the leading directories itself.
                let relative = sprintf "%s/%s" stagingDirName (string (Guid.NewGuid ()))
                let staging = sprintf "%s/%s" reposDir relative
                // `--template=` is empty deliberately: git's default templates are a
                // set of `.git/hooks/*.sample` files, and srt's macOS profile denies
                // every write under `**/.git/hooks/**` unconditionally — so the copy
                // that populates them is what fails, and a clone that asks for no
                // templates never attempts it. Nothing here wants a hooks directory
                // anyway: repo-controlled execution is off by construction.
                match! runOk unconfined (Some (lenderFor caller)) [ "clone"; "--no-recurse-submodules"; "--template="; config.CloneUrl repo; relative ] with
                | Error e ->
                    // git removes a target it created itself, but not one it was killed
                    // out of. Either way the staging area is ours to leave clean.
                    Fable.NodeExtras.Files.removeTree staging
                    return Error e
                | Ok _ ->
                    // The owner directory is the rename's destination PARENT, and git made
                    // it inside the staging area rather than here.
                    Fs.ensureDir (sprintf "%s/%s" reposDir (RepoRef.owner repo))
                    Fs.rename staging (pathOf repo)
                    return! Telemetry.spanResult config.Tracer "repo.listing" [] (listingOf repo)
            }

        /// The add as the log tells it: `RepoAdded` the moment it is asked for, so the
        /// timeline shows it before the seconds the clone takes, and then the clone's outcome
        /// under the same id — `RepoCloned`, or `RepoCloneFailed` with the reason. Every
        /// failure after the add is recorded, so no add is left reading as still cloning.
        let cloneIntoPlace (caller: RepoCaller) (repo: RepoRef) : Async<Result<Recorded<RepoListing>, string>> =
            async {
                let messageId = mintMessageId ()
                do!
                    Telemetry.span
                        config.Tracer
                        "repo.record_added"
                        []
                        (append caller.Actor (SessionEvent.RepoAdded { MessageId = messageId; Repo = repo; Actor = caller.Actor }))
                // A throw (the rename, say) is a failure like any other: an add that nothing
                // resolves reads as cloning forever.
                let! outcome = Async.Catch (cloneInPlace caller repo)
                let cloned =
                    match outcome with
                    | Choice1Of2 result -> result
                    | Choice2Of2 ex -> Error ex.Message
                match cloned with
                | Error e ->
                    do!
                        append
                            caller.Actor
                            (SessionEvent.RepoCloneFailed { MessageId = messageId; Repo = repo; Reason = e; Actor = caller.Actor })
                    return Error e
                | Ok listing ->
                    do!
                        Telemetry.span
                            config.Tracer
                            "repo.record"
                            []
                            (append
                                caller.Actor
                                (SessionEvent.RepoCloned
                                    { MessageId = messageId
                                      Repo = repo
                                      Branch = listing.Branch
                                      Actor = caller.Actor
                                      AgentsMd = agentsMdOf repo
                                      Skills = skillsOf repo }))
                    return Ok { Recorded.Answer = listing; Recorded.RecordedAs = Some messageId }
            }

        /// Clones in flight, keyed by repo. A clone is not instant, and the same repo asked
        /// for twice while the first is still running used to start a SECOND clone into the
        /// same directory. Joining the first is both correct and the answer the caller
        /// wanted — `Broker`'s token refresh is this shape for the same reason.
        let cloning = Dictionary<string, JS.Promise<Result<Recorded<RepoListing>, string>>> ()

        let addRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<Recorded<RepoListing>, string>> =
            Telemetry.spanResult
                config.Tracer
                "repo.add"
                [ "yession.repo", Telemetry.AttributeValue.String (RepoRef.value repo)
                  "yession.repo.present", Telemetry.AttributeValue.Bool (present repo) ]
            <| async {
                if present repo then
                    // Already here: answer with the current state and record nothing —
                    // a repeated add is a question, not an act. `present` can be trusted to
                    // mean WHOLE now, which is what the staging rename above buys.
                    match! listingOf repo with
                    | Ok listing ->
                        let! known = recordedRepos ()
                        if List.contains repo known then
                            return Ok { Recorded.Answer = listing; Recorded.RecordedAs = None }
                        else
                            // A whole checkout the log never heard of: a process that died
                            // between the staging rename and its `RepoCloned`. The listing is
                            // the log's, so answering "already here" and recording nothing
                            // would leave it here and invisible for good. Record it as the
                            // clone would have.
                            let messageId = mintMessageId ()
                            do! append caller.Actor (SessionEvent.RepoAdded { MessageId = messageId; Repo = repo; Actor = caller.Actor })
                            do!
                                append
                                    caller.Actor
                                    (SessionEvent.RepoCloned
                                        { MessageId = messageId
                                          Repo = repo
                                          Branch = listing.Branch
                                          Actor = caller.Actor
                                          AgentsMd = agentsMdOf repo
                                          Skills = skillsOf repo })
                            return Ok { Recorded.Answer = listing; Recorded.RecordedAs = Some messageId }
                    | Error e ->
                        // The one bad state the rename cannot prevent, because it predates
                        // it: a checkout an interrupted clone left at the visible path. git
                        // describes it as `ambiguous argument 'HEAD'`, which reads as "this
                        // repo is empty" and sent a whole session looking for the wrong
                        // fault. Say what it is and how to clear it instead.
                        return
                            Error (
                                sprintf
                                    "%s is already checked out here, but git cannot read that checkout: %s. An interrupted clone leaves a checkout like this. Use remove_repo on it, then add it again."
                                    (RepoRef.value repo)
                                    e)
                elif Fs.lexists (pathOf repo) then
                    // Something is at the checkout's path that `present` will not call a
                    // checkout. A clone would fail at the rename, and git must not be asked
                    // about it.
                    return Error (notACheckout repo)
                else
                    let key = RepoRef.value repo
                    let pending =
                        match cloning.TryGetValue key with
                        | true, running -> running
                        | _ ->
                            let running = cloneIntoPlace caller repo |> Async.StartAsPromise
                            cloning.[key] <- running
                            // Cleared on settle, so a clone that failed can be retried. The
                            // workflow answers with `Error` rather than rejecting, so this
                            // runs on both outcomes.
                            running.``then`` (fun outcome -> cloning.Remove key |> ignore; outcome) |> ignore
                            running
                    return! Interop.awaitPromise pending
            }

        let listRepos () : Async<Result<RepoRead list, string>> =
            async {
                let! refs = recordedRepos ()
                let mutable reads = []
                for ref in refs do
                    match! listingOf ref with
                    | Ok listing -> reads <- reads @ [ RepoRead.Listed listing ]
                    | Error reason ->
                        reads <- reads @ [ RepoRead.Unreadable { Repo = ref; Path = visiblePathOf ref; Reason = reason } ]
                return Ok reads
            }

        let switchBranch (caller: RepoCaller) (repo: RepoRef) (branch: string) (create: bool) : Async<Result<Recorded<RepoListing>, string>> =
            requirePresent repo (fun () ->
                async {
                    match validBranchName branch with
                    | Error e -> return Error e
                    | Ok branch ->
                        let args =
                            [ "-C"; pathOf repo; "switch" ] @ (if create then [ "-c" ] else []) @ [ branch ]
                        match! runOk confined None args with
                        | Error e -> return Error e
                        | Ok _ ->
                            match! listingOf repo with
                            | Error e -> return Error e
                            | Ok listing ->
                                let messageId = mintMessageId ()
                                do! append caller.Actor (SessionEvent.RepoBranchSwitched { MessageId = messageId; Repo = repo; Branch = listing.Branch; Created = create; Actor = caller.Actor })
                                return Ok { Recorded.Answer = listing; Recorded.RecordedAs = Some messageId }
                })

        let fetchRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    match! runOk confined (Some (lenderFor caller)) [ "-C"; pathOf repo; "fetch"; "--prune"; "--no-recurse-submodules"; "origin" ] with
                    | Error e -> return Error e
                    | Ok run ->
                        // Fetch narrates on stderr; an up-to-date fetch says nothing.
                        let said = (run.Stderr + run.Stdout).Trim ()
                        return Ok (if said = "" then "already up to date" else capText outputLimit said)
                })

        let inspect (args: string -> string list) (repo: RepoRef) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    match! runOk confined None (args (pathOf repo)) with
                    | Error e -> return Error e
                    | Ok run ->
                        let said = run.Stdout.Trim ()
                        return Ok (if said = "" then "(clean: nothing to show)" else capText outputLimit said)
                })

        let removeRepo (caller: RepoCaller) (repo: RepoRef) (force: bool) : Async<Result<string, string>> =
            async {
                // The log's repos are the listing's, so one it records is removable even with
                // its directory gone: the listing shows it as unreadable, and this is the verb
                // that row points at.
                let! known = recordedRepos ()
                if not (present repo || List.contains repo known) then
                    return Error (sprintf "repo %s is not in this session. Use add_repo first." (RepoRef.value repo))
                else
                    let remove () =
                        async {
                            Fable.NodeExtras.Files.removeTree (pathOf repo)
                            do!
                                append
                                    caller.Actor
                                    (SessionEvent.RepoRemoved
                                        { MessageId = mintMessageId (); Repo = repo; Actor = caller.Actor })
                            return Ok (visiblePathOf repo)
                        }
                    // Uncommitted work is the one thing removal cannot undo: `add_repo` brings
                    // back the commits and nothing else. So it is refused, and `force` is a
                    // second decision — taken here, with the checkout, because a caller who
                    // could delete without asking is the caller this exists to stop.
                    match! listingOf repo with
                    | Ok listing when listing.Dirty && not force ->
                        return
                            Error (
                                sprintf
                                    "%s has uncommitted changes. Removing it deletes them. Adding the repo again brings back only the commits. Commit or push the changes first, or pass force to delete them."
                                    (RepoRef.value repo))
                    | Ok _ -> return! remove ()
                    // git cannot read the checkout: exactly what an interrupted clone leaves
                    // behind, and exactly what `add_repo` points at this verb to clear. There
                    // is no dirtiness to protect, because there is nothing git can tell us
                    // about — refusing here would leave that state with no way out.
                    | Error _ -> return! remove ()
            }

        { AddRepo = addRepo
          ListRepos = listRepos
          SwitchBranch = switchBranch
          FetchRepo = fetchRepo
          RepoStatus = inspect (fun path -> [ "-C"; path; "status"; "--porcelain=v1"; "--branch" ])
          RepoLog = inspect (fun path -> [ "-C"; path; "log"; "--oneline"; "-30" ])
          // A diff driver is a program the config names, and the config is writable by every
          // work sandbox. Both flags turn every driver off, whatever the config says.
          RepoDiff = inspect (fun path -> [ "-C"; path; "diff"; "--no-ext-diff"; "--no-textconv" ])
          RemoveRepo = removeRepo })

/// The agent-facing capability set for one turn: events attribute the AGENT (it is
/// the acting party), the token is the TURN HUMAN's (Plan 08 — no borrowing across
/// actors, and the agent has no scope of its own).
let agentCaller (turnActor: Principal) : RepoCaller =
    { Actor = ActorRef.Agent; Credential = CredentialFor.Person turnActor }

// --- the `repos` query (Plan 15) ----------------------------------------------------------
// What was the Repos PANEL is now a registered query, and the panel's three write actions
// are gone: a human who wants a repo added asks the agent, and the mutation lands in the
// timeline attributed. What survives is exactly the half worth keeping — the listing: the
// repos the log records, each as git reads it, so a row can never disagree with `git status`.

let queryName : QueryName =
    match QueryName.create "repos" with
    | Ok name -> name
    | Error e -> failwithf "repos query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "repos"
      Description =
        "The repos checked out in this session. Each row shows the branch the repo is on and \
         whether it has uncommitted changes. It also shows the repo's path from where the \
         default sandbox's terminals and file tools start (`.` when they start in it). The rows \
         come from the checkouts themselves, so they always agree with git. A checkout git \
         cannot read has a row of its own with git's reason under `problem`, and the rows \
         around it are still read."
      Shape =
        Rows
            [ QueryColumn.create "repo" "repo"
              QueryColumn.create "branch" "branch"
              QueryColumn.create "dirty" "uncommitted changes"
              QueryColumn.create "path" "path"
              QueryColumn.create "problem" "problem" ]
      Legend = [] }

/// Register the listing as a query. The service keeps its typed `ListRepos` — the query
/// is a projection of it, not a replacement — so the shape lives beside the thing it
/// describes rather than in the composition root.
let query (service: ReposService) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                match! service.ListRepos () with
                | Error e -> return Error e
                | Ok reads ->
                    return
                        Ok (RowsOf (
                            reads
                            |> List.map (fun read ->
                                match read with
                                | RepoRead.Listed listing ->
                                    [ "repo", CellText (RepoRef.value listing.Repo)
                                      "branch", CellText listing.Branch
                                      "dirty", CellFlag listing.Dirty
                                      "path", CellText listing.Path
                                      "problem", CellAbsent ]
                                | RepoRead.Unreadable unreadable ->
                                    [ "repo", CellText (RepoRef.value unreadable.Repo)
                                      "branch", CellAbsent
                                      "dirty", CellAbsent
                                      "path", CellText unreadable.Path
                                      "problem", CellText unreadable.Reason ])))
            } }
