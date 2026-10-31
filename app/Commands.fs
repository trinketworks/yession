module Yession.Host.Commands

// The session's gated COMMANDS, both halves in one place (Plan 14, Plan 15 stage 2/3b).
//
// A gated command is two functions that only make sense together: the one a turn calls,
// which encodes the arguments and hands them to the gate, and the one the GATE calls once a
// verdict is in, which decodes those same arguments and carries the act out. They are joined
// by nothing but a string — the tool name — and by the encoding of the argument list, so a
// rename or a re-ordering on one side is a runtime failure on the other.
//
// Both used to live in `SessionMain.fs`, which is an entry: it has top-level effects, so
// nothing may reference it and no test can reach what it holds. The pair therefore had the
// shape the colocation rule warns about — a decision taken in the composition root, where the
// cheap tier cannot see it. They are here, beside each other, so a harness can drive a tool
// call against the SAME bindings a turn uses rather than against a re-statement of them.
//
// What stays in the entry is composition: which services exist, and handing the table to the
// gate the Host owns.
//
// Every command here is ENSURE-shaped: re-running one with the same arguments converges on the
// same state, records nothing, and says so. That is not politeness — the declarative form of
// this API is a fold of a config file into these commands on every boot, so a command that
// accumulated instead of converging would clone a repo twice, or restart a sandbox somebody is
// working in, each time the session came up. `add_repo` answers with the current listing when
// the checkout is already there; `start_work_sandbox` hands back the running sandbox and
// refuses only when the configuration differs.

open Yession.Domain
open Yession.Codecs

open Yession.Domain.Sandboxes
open Yession.Domain.Content
open Yession.Domain.Agent
open Yession.Domain.Files
open Yession.Domain.Tools
open Yession.Domain.Repos
open Yession.Session
open Yession.Domain.Prs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What the commands need from the session around them, as getters — the services are
/// composed during boot and the table is built once, so a value read here would be the one
/// that existed before the session did.
type CommandServices =
    { /// The repo manager, absent when the session could not start one.
      Repos : unit -> Repos.ReposService option
      /// The session's named WorkSandboxes.
      Sandboxes : unit -> WorkSandboxes.WorkSandboxes
      /// A repo's checkout at its one absolute path (`Sandboxes.checkoutAt`), which a
      /// repo-owned declaration's `workdir:` and `build:` context both resolve against.
      /// Absolute rather than a repo verb's answer, which is said relative to where the
      /// default sandbox's terminals start.
      WorkCheckout : RepoRef -> string
      /// What the checkouts declare their sandboxes to be, as the last fold read it (Plan 27),
      /// or why it has nothing current (`Declarations`); `Read` of nothing in a session with
      /// nothing to fold.
      ///
      /// Needed because a repo's sandbox is its FILE's to describe, and the tool that names
      /// one carries a declaration with nothing in it — so a start given the caller's
      /// declaration verbatim came up, or refused, as something the repo never wrote.
      DeclaredSandboxes : unit -> Declarations
      /// The terminal manager, which owns the shell profile (Plan 25).
      Terminals : unit -> SessionTerminals.SessionTerminals
      /// The files inside each sandbox, for the two commands that change one.
      Files : unit -> SessionFiles.SessionFiles
      /// The session's artifacts, for the one command that adds to them.
      Artifacts : unit -> Artifacts.SessionArtifacts
      /// Queueing a command as a recorded block — the same door `execute_command` goes
      /// through, which is the point: a sandbox's declared `setup:` is a command somebody
      /// can watch, edit before it runs, and read the outcome of afterwards, not a private
      /// spawn this layer arranges on the side.
      RunCommand : unit -> TerminalCommands.TerminalCommands
      /// The pull request verbs — watching, and opening one — absent when the session
      /// could not start the poller they share.
      Prs : unit -> PrWatches.PrService option
      /// Say a query's answer changed. A command is the only thing that can change one, so
      /// a command is the only thing that has to say so — nothing polls.
      Invalidate : QueryName -> unit
      /// Record that a repo's `setup:` was queued, or could not be — the one thing a command
      /// here puts on the log directly, because it is the one thing no service below it
      /// knows: the sandbox manager did not queue it and the terminal does not know why it
      /// exists.
      ///
      /// A `Result` rather than the fact's own two optionals, so a caller cannot build the
      /// pair that means nothing; the Host maps it, in one place.
      NoteSetup : SandboxRef -> string -> Result<QueueId, string> -> ActorRef -> Async<unit>
      /// Re-read every checkout's `yession.yaml` and ensure what it declares (Plan 27), on
      /// the authority of whoever ran the verb.
      ///
      /// Called by the three verbs that change what checkouts exist or what is in them, and
      /// by nothing else. It is safe to call whenever one of them succeeds because the fold
      /// is idempotent — a declaration that is already a running sandbox is an ask that
      /// changes nothing and records nothing, which is what every mutating command being
      /// ensure-shaped bought.
      ///
      /// The fold cause says what the verb did, so a sandbox it brings up can point back to it.
      Refold : FoldCause -> CredentialFor -> Async<unit>
      /// Record that a tab was put in front of the people here, or taken back — the other
      /// thing a command here puts on the log directly, and for the same reason `NoteSetup`
      /// does: there is no service under it. A tab is a thing on somebody's screen, and the
      /// log is how it reaches one.
      ///
      /// `focus` rides the opening rather than being a verb of its own, because it is one
      /// fact about one act: whether this takes the reader's screen or waits to be chosen.
      NoteTabOpened : ViewRef -> bool -> ActorRef -> Async<unit>
      NoteTabClosed : ViewRef -> ActorRef -> Async<unit> }

let private encodeArgs (values: string list) : string = Codec.toString CommandGates.args values

/// Re-read every checkout's `yession.yaml` once a verb has actually changed what checkouts
/// exist or what is in them (Plan 27).
///
/// On SUCCESS only: a verb that refused changed nothing, so a fold behind a refusal would be
/// work nobody asked for. And it wraps rather than being written into each body because the
/// three verbs it applies to have nothing else in common with it — what a fold reads is none
/// of their business, and the day a fourth verb changes a checkout, this is the one thing it
/// has to be given.
///
/// The verb answers with the item it recorded, when it recorded one, so a sandbox the fold
/// brings up for this repo can say it came up because of it.
let private refoldingAs
    (cause: RepoRef -> MessageId option -> CredentialFor -> FoldCause)
    (services: CommandServices)
    (invocation: GatedInvocation)
    (repo: RepoRef)
    (outcome: Async<Result<'a * MessageId option, string>>)
    : Async<Result<'a, string>> =
    async {
        match! outcome with
        // Whoever the verb ran on the authority of: a sandbox the fold starts from a file it
        // picks up is started on THEIR authority, as the verb itself was.
        | Ok (answer, recordedAs) ->
            let credential = Authority.credential invocation.Authority
            do! services.Refold (cause repo recordedAs credential) credential
            return Ok answer
        | Error e -> return Error e
    }

let private andRefold services invocation repo outcome =
    refoldingAs (fun repo recordedAs _ -> FoldCause.Changed (repo, recordedAs)) services invocation repo outcome

/// `andRefold`, for the one verb that OFFERS its person's trust: adding a repo. When their own
/// record already trusts it with everything it asks, the fold runs it with their GitHub and
/// asks nobody. Removing a repo or switching its branch offers nothing (`FoldCause.Offered`).
/// The agent's add is its turn's person's, as every other credential of its turn is.
let private andRefoldOffering services invocation repo outcome =
    refoldingAs
        (fun repo recordedAs credential ->
            match CredentialFor.person credential with
            | Some person -> FoldCause.Offered (repo, recordedAs, person)
            | None -> FoldCause.Changed (repo, recordedAs))
        services
        invocation
        repo
        outcome

/// Invalidate a query once a command has actually changed its answer.
let private andPublish
    (services: CommandServices)
    (name: QueryName)
    (outcome: Async<Result<'a, string>>)
    : Async<Result<'a, string>> =
    async {
        let! result = outcome
        match result with
        | Ok _ -> services.Invalidate name
        | Error _ -> ()
        return result
    }

// The gated commands, by MCP tool name. The dispatch table's keys and every call site
// live in this one file, so the name is agreed where it is used — the settings surface and
// boot configuration that used to read a shared catalogue are gone (Plan 23).
let private addRepoTool = "add_repo"
let private removeRepoTool = "remove_repo"
let private switchBranchTool = "switch_branch"
let private createPrTool = "create_pr"
let private mergePrTool = "merge_pr"
let private unmergePrTool = "unmerge_pr"
let private readyPrTool = "ready_pr"
let private draftPrTool = "draft_pr"
let private watchPrTool = "watch_pr"
let private unwatchPrTool = "unwatch_pr"
let private startWorkSandboxTool = "start_work_sandbox"
let private stopWorkSandboxTool = "stop_work_sandbox"
let private setShellProfileTool = "set_shell_profile"
let private editFileTool = "edit_file"
let private writeFileTool = "write_file"
let private shareArtifactTool = "share_artifact"

/// One `start_work_sandbox` call, built where the dispatch entry that reads it lives.
///
/// The encoding is private and stays private: a caller that spelled these arguments itself
/// would be a second spelling of them, and the two would disagree the first time one
/// changed. Every route to this verb — the agent's capability below, the fold over
/// `yession.yaml` — goes through here.
///
/// The cause rides with the arguments, because the start is where it is recorded and the
/// gate is the only road there.
let startWorkSandboxCall (authority: Authority) (causedBy: Cause option) (sandbox: SandboxRef) (decl: SandboxDecl) : GatedCall =
    { Tool = startWorkSandboxTool
      Args =
        encodeArgs (
            [ SandboxRef.render sandbox; ConfigFile.encodeSandbox decl ]
            @ (causedBy |> Option.map (Codec.toString Codec.cause) |> Option.toList))
      Summary =
        match decl.Uses with
        | [] -> sprintf "start_work_sandbox %s" (SandboxRef.render sandbox)
        | uses ->
            sprintf
                "start_work_sandbox %s using %s"
                (SandboxRef.render sandbox)
                (uses |> List.map ResourceName.value |> String.concat ", ")
      Authority = authority }

/// `startWorkSandboxCall`'s arguments read back: the sandbox, its declaration, and what caused
/// the start when anything did. Beside the encoding so the two cannot drift apart.
let private readStartArgs (values: string list) : Result<string * string * Cause option, string> =
    match values with
    | [ name; declared ] -> Ok (name, declared, None)
    | [ name; declared; cause ] ->
        Codec.fromString Codec.cause cause
        |> Result.map (fun c -> name, declared, Some c)
        |> Result.mapError (sprintf "not a cause: %s")
    | other ->
        Error (
            sprintf
                "start_work_sandbox got %d arguments. It takes a sandbox, a declaration and an optional cause"
                (List.length other))

/// The directory a CAUSED `set_shell_profile` carries: present or absent as a value, because
/// the caused form has a fixed arity and its absence cannot be a shorter list.
let private profileDirectory : Codec<string option> =
    { Encode = Encode.option Encode.string; Decode = Decode.option Decode.string }

/// One `set_shell_profile` call, built where the dispatch entry that reads it lives — for
/// the agent's capability and for the fold applying a repo's `session.shell-profile`.
///
/// An uncaused call is the shape it always was: one argument clears, two set. A caused one
/// is three — the sandbox, the directory as a value, the cause — so the arity alone says
/// which reading applies and a directory can never be read as a cause.
let setShellProfileCall (authority: Authority) (causedBy: Cause option) (sandbox: SandboxRef) (cwd: string option) : GatedCall =
    let name = SandboxRef.render sandbox
    { Tool = setShellProfileTool
      Args =
        encodeArgs (
            match causedBy with
            | None -> name :: Option.toList cwd
            | Some cause -> [ name; Codec.toString profileDirectory cwd; Codec.toString Codec.cause cause ])
      Summary =
        match cwd with
        | Some cwd -> sprintf "set_shell_profile %s -> %s" name cwd
        | None -> sprintf "set_shell_profile %s -> wherever the sandbox puts them" name
      Authority = authority }

/// `setShellProfileCall`'s arguments read back: the sandbox, the directory, and the cause.
let private readProfileArgs (values: string list) : Result<string * string option * Cause option, string> =
    match values with
    | [ name ] -> Ok (name, None, None)
    | [ name; cwd ] -> Ok (name, Some cwd, None)
    | [ name; cwd; cause ] ->
        match Codec.fromString profileDirectory cwd, Codec.fromString Codec.cause cause with
        | Ok cwd, Ok cause -> Ok (name, cwd, Some cause)
        | Error e, _ -> Error (sprintf "not a directory: %s" e)
        | _, Error e -> Error (sprintf "not a cause: %s" e)
    | other ->
        Error (
            sprintf
                "set_shell_profile got %d arguments. It takes a sandbox name and an optional directory"
                (List.length other))

/// How each gated command is actually carried out, by tool name (Plan 15, stage 3b).
///
/// A malformed invocation FAILS rather than guessing: "run something adjacent to what was
/// asked for" is the one outcome a gate must never produce.
///
/// The arguments are read once, here, before any verb sees them, and arguments that will not
/// read are refused as exactly that. They used to decode to `[]` when they did not decode at
/// all, so every verb refused them with its own count — "takes one repo, got 0 arguments" —
/// which is the sentence for a call that carried nothing, about a call that carried
/// something nobody could read. Each verb now matches on a list that was really there.
let dispatch (services: CommandServices) : CommandDispatch =
    // Whose credential, asked once: the borrowed authority when there is one, the author
    // otherwise. It used to be a `defaultArg` per call site with `ActorRef.Agent` written in
    // as the fallback — which was right only because the agent is what authored every one of
    // these, a coincidence each site had to keep re-establishing.
    let repoCaller (invocation: GatedInvocation) : Repos.RepoCaller =
        { Actor = Authority.author invocation.Authority
          Credential = Authority.credential invocation.Authority }
    // A sandbox is stopped by an ACTOR. A start takes the whole authority, but only to say
    // who it was for: what its blocks spend is each block's own act's credential.
    let sandboxCaller (invocation: GatedInvocation) : ActorRef = Authority.author invocation.Authority
    Map.ofList
        [ addRepoTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Repos (), args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefoldOffering services invocation repo (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.AddRepo (repoCaller invocation) repo with
                                    | Error e -> return Error e
                                    | Ok recorded ->
                                        let listing = recorded.Answer
                                        // What this verb KNOWS, and nothing else. It used to
                                        // end with "set_shell_profile with that path", which
                                        // is advice about a tool a repo verb has no business
                                        // knowing — and the path it named was the default
                                        // sandbox's, so an agent that followed it into a
                                        // container the repo declared pointed a profile at
                                        // somewhere that does not exist there and spent six
                                        // calls finding out.
                                        //
                                        // Where the work is, and what to do about it, are the
                                        // SANDBOX's to say: its start names the checkout as
                                        // that sandbox sees it, and a repo that wants to steer
                                        // whoever reads it writes so in its `description:`.
                                        // Both reach a running turn, which is the whole reason
                                        // this sentence existed and the only reason it worked.
                                        //
                                        // What stays is WHERE this path is said from, and that
                                        // is a fact rather than advice — the only fact here
                                        // nothing else supplies. The checkout has one absolute
                                        // path in every sandbox (`Sandboxes.checkoutAt`), but
                                        // this answer is relative to where the default
                                        // sandbox's terminals start, and a sandbox the repo
                                        // declares starts somewhere else. Unlabelled, a reader
                                        // took it as the dev sandbox's path, pointed that
                                        // sandbox's profile at it, and spent two calls undoing
                                        // it.
                                        return
                                            Ok (
                                                sprintf
                                                    "added %s. That path is relative to where the DEFAULT sandbox's terminals start. The checkout has one absolute path, the same in every sandbox. A sandbox that this repo declares names it when it starts. Everyone in this session shares the checkout."
                                                    (RepoListing.describe listing),
                                                recorded.RecordedAs)
                                }))
                | Some _, other -> return Error (sprintf "add_repo got %d arguments. It takes one repo" (List.length other))
            }

          removeRepoTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Repos (), args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo; force ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefold services invocation repo (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.RemoveRepo (repoCaller invocation) repo (force = "true") with
                                    | Error e -> return Error e
                                    | Ok path ->
                                        // Plan 25's upstream half, with a caller at last: a
                                        // profile pointing inside a tree that has gone would
                                        // send every future terminal somewhere that no longer
                                        // exists. The repo service contributes the one fact
                                        // only it has — the path, as a terminal saw it — and
                                        // the terminal manager decides which profiles that
                                        // invalidates. Nothing is computed here.
                                        let! cleared =
                                            (services.Terminals ())
                                                .ClearProfilesUnder invocation.Authority path
                                        if not (List.isEmpty cleared) then
                                            services.Invalidate ShellProfile.queryName
                                        let profiles =
                                            match cleared with
                                            | [] -> ""
                                            | names ->
                                                sprintf
                                                    " New terminals in %s start where the sandbox puts them again."
                                                    (names |> List.map SandboxRef.render |> String.concat ", ")
                                        return
                                            Ok (
                                                sprintf
                                                    "removed %s. The checkout is gone from this session and from the work environment.%s"
                                                    (RepoRef.value repo)
                                                    profiles,
                                                // A removal starts nothing for this repo, so
                                                // there is nothing to point back to it.
                                                None)
                                }))
                | Some _, other ->
                    return Error (sprintf "remove_repo got %d arguments. It takes a repo and a flag" (List.length other))
            }

          switchBranchTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Repos (), args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo; branch; create ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefold services invocation repo (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.SwitchBranch (repoCaller invocation) repo branch (create = "true") with
                                    | Error e -> return Error e
                                    | Ok recorded ->
                                        return Ok (sprintf "now on %s" (RepoListing.describe recorded.Answer), recorded.RecordedAs)
                                }))
                | Some _, other ->
                    return Error (sprintf "switch_branch got %d arguments. It takes a repo, a branch and a flag" (List.length other))
            }

          createPrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                // A description is present or it is not, and the ARITY carries that, like
                // `set_shell_profile`'s directory below: five arguments is a pull request with
                // no body, six is one with the body last. Never an empty string standing in
                // for the absence.
                let parsed =
                    match args with
                    | [ repo; head; onto; title; draft ] -> Ok (repo, head, onto, title, draft, None)
                    | [ repo; head; onto; title; draft; body ] -> Ok (repo, head, onto, title, draft, Some body)
                    | other -> Error other
                match services.Prs (), parsed with
                | None, _ -> return Error "this session cannot open pull requests"
                | Some _, Error other ->
                    return
                        Error (
                            sprintf
                                "create_pr got %d arguments. It takes a repo, a head, a base, a title, a flag and an optional body"
                                (List.length other))
                | Some service, Ok (repo, head, onto, title, draft, body) ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        // Re-made from the encoded arguments rather than carried: the gate is
                        // the boundary the act crosses, and a draft assembled on the far side
                        // of it is one this side validated again. Nothing is published here —
                        // no watch begins, so there is no query whose answer this changed.
                        match PrDraft.create repo head onto title body (draft = "true") with
                        | Error e -> return Error e
                        | Ok drafted -> return! service.Create invocation.Authority drafted
            }

          mergePrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot merge pull requests"
                | Some service, [ repo; number; method ] ->
                    match RepoRef.create repo, System.Int32.TryParse number, PrMergeMethod.create method with
                    | Error e, _, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _), _ -> return Error "not a pull request number"
                    | _, _, Error e -> return Error e
                    | Ok repo, (true, number), Ok method ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr ->
                            // The verb begins a watch on what it armed, so the query the
                            // watch reads back through has moved.
                            return!
                                andPublish services PrWatches.queryName (
                                    service.Merge invocation.Authority pr method)
                | Some _, other ->
                    return Error (sprintf "merge_pr got %d arguments. It takes a repo, a number and a method" (List.length other))
            }

          unmergePrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot merge pull requests"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr -> return! service.Unmerge (Authority.credential invocation.Authority) pr
                | Some _, other ->
                    return Error (sprintf "unmerge_pr got %d arguments. It takes a repo and a number" (List.length other))
            }

          readyPrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot mark pull requests ready"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr -> return! service.Ready (Authority.credential invocation.Authority) pr
                | Some _, other ->
                    return Error (sprintf "ready_pr got %d arguments. It takes a repo and a number" (List.length other))
            }

          draftPrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot make pull requests drafts"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr -> return! service.Draft (Authority.credential invocation.Authority) pr
                | Some _, other ->
                    return Error (sprintf "draft_pr got %d arguments. It takes a repo and a number" (List.length other))
            }

          watchPrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot watch pull requests"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr ->
                            return!
                                andPublish services PrWatches.queryName (
                                    service.Watch invocation.Authority pr)
                | Some _, other ->
                    return Error (sprintf "watch_pr got %d arguments. It takes a repo and a number" (List.length other))
            }

          unwatchPrTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match services.Prs (), args with
                | None, _ -> return Error "this session cannot watch pull requests"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr ->
                            return!
                                andPublish services PrWatches.queryName (
                                    service.Unwatch (Authority.author invocation.Authority) pr)
                | Some _, other ->
                    return Error (sprintf "unwatch_pr got %d arguments. It takes a repo and a number" (List.length other))
            }

          startWorkSandboxTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                // A sandbox and a DECLARATION, because a declaration is what both callers
                // have: the agent's names some credentials, a repo's file names everything.
                // One shape, so the declarative route and the interactive one cannot
                // diverge — which is the reason this gate is a capability at all.
                match readStartArgs (args) with
                | Ok (name, declared, causedBy) ->
                    match SandboxRef.parse name with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        match ConfigFile.parseSandbox declared with
                        | Error e -> return Error (sprintf "not a sandbox declaration: %s" e)
                        | Ok decl ->
                            // The checkout is DERIVED from the sandbox's scope, never
                            // carried: a gated call that could name a checkout could name
                            // any directory on this host.
                            let checkout =
                                match SandboxRef.scope name, services.Repos () with
                                | RepoOwned repo, Some _ -> Some (services.WorkCheckout repo)
                                | RepoOwned _, None
                                | SessionOwned, _ -> None
                            match SandboxDecl.toRequest checkout decl with
                            | Error e -> return Error e
                            | Ok request ->
                                match! (services.Sandboxes ()).Ensure invocation.Authority causedBy name request with
                                | Error e -> return Error e
                                | Ok outcome ->
                                    let entry = WorkSandboxes.SandboxOutcome.sandbox outcome
                                    services.Invalidate WorkSandboxes.queryName
                                    // What the repo declared to make this sandbox ready,
                                    // queued as a block anyone can watch — the same door
                                    // `execute_command` goes through, so it is on the record
                                    // and its failure is readable where every other
                                    // command's is.
                                    //
                                    // Only for a sandbox this ask STARTED. The fold re-asks
                                    // at boot and after every repo verb, and a setup block
                                    // appearing each time somebody touched a checkout is
                                    // noise nobody asked for.
                                    //
                                    // BACKGROUND, and not awaited: a setup worth declaring
                                    // is the slow thing the first command would otherwise
                                    // pay for, and blocking here would move that cost onto
                                    // the boot fold instead. The terminal serialises, so the
                                    // agent's next command in this sandbox queues behind it
                                    // without anyone arranging that.
                                    let! setup =
                                        match entry.Request.Spec.Setup, outcome with
                                        | Some command, WorkSandboxes.SandboxStarted _ ->
                                            async {
                                                match!
                                                    (services.RunCommand ()).Execute
                                                        { CommandRequest.ofCommand command with
                                                            Target = Some (InSandbox entry.Ref)
                                                            Background = true }
                                                        invocation.Authority
                                                    with
                                                // Recorded, not just returned. This answer
                                                // reaches whoever called `start_work_sandbox`
                                                // — and on the fold path that is the fold,
                                                // which reads it and drops it, so the one
                                                // sentence explaining a terminal the next
                                                // turn finds busy was written and thrown
                                                // away. The event reaches both audiences:
                                                // the timeline a person reads, and — through
                                                // the audit seam — the turn that is running.
                                                | Ok outcome ->
                                                    do!
                                                        services.NoteSetup
                                                            entry.Ref
                                                            command
                                                            (Ok outcome.Handle)
                                                            (Authority.author invocation.Authority)
                                                    return ". It is running its setup"
                                                // Said, never fatal: the sandbox is up, and
                                                // a setup that could not be QUEUED is worth
                                                // reading rather than a start that reports
                                                // failure for something already running.
                                                | Error reason ->
                                                    do!
                                                        services.NoteSetup
                                                            entry.Ref
                                                            command
                                                            (Error reason)
                                                            (Authority.author invocation.Authority)
                                                    return sprintf ". Its setup could not be queued: %s" reason
                                            }
                                        | _ -> async { return "" }
                                    let forwarding =
                                        if Map.isEmpty entry.Forwarded then "nothing forwarded into it"
                                        else "forwarding " + WorkSandboxes.ForwardedRoutes.describe entry.Forwarded
                                    return
                                        Ok (
                                            sprintf
                                                "sandbox '%s' is up on %s, %s. Use execute_command to run things in it%s"
                                                (SandboxRef.render entry.Ref)
                                                entry.Backend
                                                forwarding
                                                setup)
                | Error e -> return Error e
            }

          stopWorkSandboxTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match args with
                | [ name ] ->
                    match SandboxRef.parse name with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        match! (services.Sandboxes ()).Stop (sandboxCaller invocation) name with
                        | Error e -> return Error e
                        | Ok () ->
                            services.Invalidate WorkSandboxes.queryName
                            return Ok (sprintf "sandbox '%s' is stopped. Anything that was running in it is gone" (SandboxRef.render name))
                | other -> return Error (sprintf "stop_work_sandbox got %d arguments. It takes one sandbox name" (List.length other))
            }

          setShellProfileTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                // Both halves of this gated command live in one file (`setShellProfileCall`,
                // `readProfileArgs`) so the encode and this decode stay the one shape.
                match readProfileArgs args with
                | Error e -> return Error e
                | Ok (rawName, cwd, causedBy) ->
                    match SandboxRef.parse rawName with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        return!
                            andPublish services ShellProfile.queryName (
                                (services.Terminals ()).SetProfile invocation.Authority causedBy name cwd)
            }

          // The two file commands (the file verbs). Acts on the shared checkout, so they
          // pass the gate like every other command here; the sandbox does the reading and
          // writing (`SessionFiles`), and this side only decodes what the other encoded.
          editFileTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match args with
                | [ rawName; path; oldText; newText; replaceAll ] ->
                    match SandboxRef.parse rawName with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok sandbox ->
                        match!
                            (services.Files ()).Edit
                                (Authority.author invocation.Authority)
                                { FileEditRequest.Sandbox = sandbox
                                  FileEditRequest.Path = path
                                  FileEditRequest.OldText = oldText
                                  FileEditRequest.NewText = newText
                                  FileEditRequest.ReplaceAll = (replaceAll = "true") }
                        with
                        | Error reason -> return Error reason
                        | Ok edited ->
                            return
                                Ok (
                                    if edited.Replaced = 1 then
                                        sprintf "edited %s: −%d +%d lines" path edited.LinesRemoved edited.LinesAdded
                                    else
                                        sprintf
                                            "edited %s in %d places: −%d +%d lines"
                                            path
                                            edited.Replaced
                                            edited.LinesRemoved
                                            edited.LinesAdded)
                | other -> return Error (sprintf "edit_file got %d arguments. It takes a sandbox, a path, two texts and a flag" (List.length other))
            }

          writeFileTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                match args with
                | [ rawName; path; content ] ->
                    match SandboxRef.parse rawName with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok sandbox ->
                        match! (services.Files ()).Write (Authority.author invocation.Authority) sandbox path content with
                        | Error reason -> return Error reason
                        | Ok () -> return Ok (sprintf "wrote %s (%d lines)" path (List.length (FileSlice.lines content)))
                | other -> return Error (sprintf "write_file got %d arguments. It takes a sandbox, a path and the content" (List.length other))
            }

          // Sharing a file with the people here. The gate sees the source path and the name
          // asked for; which VERSION it becomes is minted on the far side, by the store, after
          // the verdict — so an approval is never for an address that has since been taken.
          shareArtifactTool,
          fun (invocation: GatedInvocation) (args: string list) ->
            async {
                let named (values: string list) =
                    match values with
                    | [ rawName; path ] -> Ok (rawName, path, None)
                    | [ rawName; path; name ] -> Ok (rawName, path, Some name)
                    | other ->
                        Error (
                            sprintf
                                "share_artifact got %d arguments. It takes a sandbox, a path and an optional name"
                                (List.length other))
                match named (args) with
                | Error e -> return Error e
                | Ok (rawName, path, name) ->
                    match SandboxRef.parse rawName with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok sandbox ->
                        match! (services.Artifacts ()).Share (Authority.author invocation.Authority) sandbox path name with
                        | Error reason -> return Error reason
                        | Ok shared ->
                            services.Invalidate Artifacts.queryName
                            // The address is the one thing the caller could not have worked
                            // out, because it did not choose it — so it is what the answer
                            // leads with.
                            return
                                Ok (
                                    sprintf
                                        "shared %s as %s (%s)"
                                        path
                                        (ArtifactRef.url shared.Ref)
                                        (ContentSize.render shared.Bytes))
            } ]
    |> Map.map (fun _ (run: GatedInvocation -> string list -> Async<Result<string, string>>) ->
        fun (invocation: GatedInvocation) ->
            match Codec.fromString CommandGates.args invocation.Args with
            | Ok args -> run invocation args
            | Error reason -> async { return Error (sprintf "the call's arguments could not be read: %s" reason) })

/// How a PERSON puts the first repo into a session — the launch surface's one act, and the
/// one human-authored repo verb there is.
///
/// Plan 15 retired the human add/remove/switch buttons because they were a second surface
/// over verbs the agent already had, mid-session, where asking costs one sentence. At a
/// session's start there is nobody to ask: no turn has run, and which repo this session is
/// FOR is the person's to say. So this is the same verb, gated and attributed the same way,
/// with a second caller rather than a second implementation — and admitted only while the
/// session has no repo. A second repo is still the agent's to add; that keeps the line Plan
/// 15 drew where it was.
///
/// Two stages, and the split is the point: ADMISSION answers at once (a peer's command
/// pump is held for the answer, and a clone is not something to hold it for), and the WORK
/// is handed back for the caller to run wherever it can wait — the Host in the background,
/// a test in line. The work is two gated calls in sequence, each waited out past the gate's
/// deadline because the next depends on it: `add_repo`, then `switch_branch` when a branch
/// other than the clone's was chosen. Both are the person's choices, so both are theirs.
///
/// Where terminals start is NOT set here. It was, as the person, which recorded a default
/// the launch chose as a choice they made. A repo that wants its checkout as the shell's
/// home says so in its `yession.yaml` (`session.shell-profile`), and the fold applies it as
/// that repo's — the one place a file's effects are authored.
[<RequireQualifiedAccess>]
type LaunchFailure =
    { /// Which of the launch's calls did not succeed, as the gate knew it.
      Tool : string
      /// Its summary — the sentence the record carries for it.
      Summary : string
      Reason : string
      /// Whether the log already says it: a clone that fails records `RepoCloneFailed`
      /// on the add's own item, and a second note under it would be one failure twice.
      Recorded : bool }

type LaunchRepo = Principal -> RepoRef -> string option -> Async<Result<Async<Result<unit, LaunchFailure>>, string>>

let launchRepo
    (tracer: Telemetry.Tracer)
    (services: CommandServices)
    (run: RunGatedCommand)
    (resume: QueueId -> Async<Result<CommandOutcome, string>>)
    : LaunchRepo =
    fun actor repo branch ->
        let authority = Authority.ofAuthor actor
        let named = [ "yession.repo", Telemetry.AttributeValue.String (RepoRef.value repo) ]
        /// One gated call, waited to a verdict. `CommandRunning` is a yield, not an outcome —
        /// the gate hands back a handle and the work runs on — so it is resumed until it is one.
        let rec settle (outcome: Result<CommandOutcome, string>) : Async<Result<string, string>> =
            async {
                match outcome with
                | Error e -> return Error e
                | Ok { Status = CommandRefusedBy (_, reason) } -> return Error (defaultArg reason "refused")
                | Ok { Status = CommandRan text } when text.StartsWith "failed: " -> return Error (text.Substring 8)
                | Ok { Status = CommandRan text } -> return Ok text
                | Ok { Status = CommandRunning; Handle = Some handle } ->
                    let! next = resume handle
                    return! settle next
                | Ok { Status = CommandRunning; Handle = None } -> return Error "the command yielded with no handle to resume it"
            }
        let launchFailed (outcome: Result<'a, LaunchFailure>) =
            match outcome with
            | Error failure -> Some (sprintf "%s: %s" failure.Tool failure.Reason)
            | Ok _ -> None
        // One span per gated call, so the gate's own cost is what is left of it once the
        // verb's span inside is subtracted.
        let gated (tool: string) (args: string list) (summary: string) : Async<Result<string, LaunchFailure>> =
            Telemetry.spanWith launchFailed tracer (sprintf "gate %s" tool) (("yession.tool", Telemetry.AttributeValue.String tool) :: named)
            <| async {
                let! outcome = run { Tool = tool; Args = encodeArgs args; Summary = summary; Authority = authority }
                match! settle outcome with
                | Ok text -> return Ok text
                // Admission found no repo here, so an `add_repo` that fails failed in its
                // clone, after its add was recorded — and the repo service recorded that.
                | Error reason ->
                    return Error { LaunchFailure.Tool = tool; Summary = summary; Reason = reason; Recorded = (tool = addRepoTool) }
            }
        // Admission and the work are two spans because they are two moments: the peer is
        // answered after the first, and the second runs on wherever the caller starts it.
        Telemetry.spanResult tracer "repo.launch.admit" named
        <| async {
            match services.Repos () with
            | None -> return Error "this session has no repos"
            | Some service ->
                match! service.ListRepos () with
                | Error e -> return Error e
                | Ok (existing :: _) ->
                    return
                        Error (
                            sprintf
                                "this session already has %s. Ask the agent to add another repo"
                                (RepoRef.value (RepoRead.repo existing)))
                | Ok [] ->
                    return
                        Ok (
                            Telemetry.spanWith launchFailed tracer "repo.launch" named
                            <| async {
                                match! gated addRepoTool [ RepoRef.value repo ] (sprintf "add_repo %s" (RepoRef.value repo)) with
                                | Error e -> return Error e
                                | Ok _ ->
                                    // What the clone came up on, read back rather than assumed:
                                    // the default branch is the provider's to say, and a choice
                                    // that names it is not a switch.
                                    let added = sprintf "add_repo %s" (RepoRef.value repo)
                                    let listingFailure (reason: string) : LaunchFailure =
                                        { LaunchFailure.Tool = addRepoTool; Summary = added; Reason = reason; Recorded = false }
                                    match! service.ListRepos () with
                                    | Error e -> return Error (listingFailure e)
                                    | Ok reads ->
                                        match reads |> List.tryFind (fun read -> RepoRead.repo read = repo) with
                                        | None ->
                                            return
                                                Error (
                                                    listingFailure (
                                                        sprintf "%s was added and is not in the listing" (RepoRef.value repo)))
                                        | Some (RepoRead.Unreadable unreadable) -> return Error (listingFailure unreadable.Reason)
                                        | Some (RepoRead.Listed listing) ->
                                            let! switched =
                                                match branch with
                                                | Some wanted when wanted <> listing.Branch ->
                                                    gated
                                                        switchBranchTool
                                                        [ RepoRef.value repo; wanted; "false" ]
                                                        (sprintf "switch_branch %s -> %s" (RepoRef.value repo) wanted)
                                                | _ -> async { return Ok "" }
                                            match switched with
                                            | Error e -> return Error e
                                            | Ok _ -> return Ok ()
                            })
        }

/// The turn's repo verbs (Plan 14), bound to the acting party. The MUTATING ones are three
/// lines each: encode the arguments, render the summary, hand both to the gate. What they
/// used to do lives in `dispatch` above, where a process that did not propose the act can
/// still reach it.
let private repoCapabilitiesFor
    (services: CommandServices)
    (turnActor: Principal)
    (capabilities: AgentCapabilities)
    : AgentCapabilities =
    match services.Repos () with
    | None -> capabilities
    | Some service ->
        let gated (tool: string) (args: string list) (summary: string) =
            capabilities.RunGated
                { Tool = tool
                  Args = encodeArgs args
                  Summary = summary
                  // The agent acts; the credential is the turn human's (Plan 08). `agentFor`
                  // TAKES that actor, so an agent-authored call with nobody's authority on it
                  // is not something this could be written to omit.
                  Authority = Authority.agentFor turnActor }
        let capabilities =
            { capabilities with
                Repos =
                  { capabilities.Repos with
                      Add =
                        fun repo ->
                          gated addRepoTool [ RepoRef.value repo ] (sprintf "add_repo %s" (RepoRef.value repo))
                      Remove =
                        fun repo force ->
                          // The cost is in the SUMMARY, because that is the sentence the classifier
                          // reads and a person watching the queue sees BEFORE it happens rather than
                          // after. A removal that would take uncommitted work with it must not look
                          // like one that would not.
                          let summary =
                              if force then sprintf "remove_repo %s (deleting uncommitted changes)" (RepoRef.value repo)
                              else sprintf "remove_repo %s" (RepoRef.value repo)
                          gated removeRepoTool [ RepoRef.value repo; (if force then "true" else "false") ] summary
                      SwitchBranch =
                        fun repo branch create ->
                          let summary =
                              if create then sprintf "switch_branch %s -> new branch %s" (RepoRef.value repo) branch
                              else sprintf "switch_branch %s -> %s" (RepoRef.value repo) branch
                          gated switchBranchTool [ RepoRef.value repo; branch; (if create then "true" else "false") ] summary
                      CreatePr =
                        fun draft ->
                          // The head, the base and the DRAFT flag are in the summary, because that
                          // is the sentence the classifier reads and a person watching the queue
                          // sees before it happens — and a pull request everybody can see is not
                          // the same act as a draft nobody is asked to review yet.
                          let summary =
                              sprintf
                                  "create_pr %s%s: %s"
                                  (PrDraft.render draft)
                                  (if draft.Draft then " as a draft" else "")
                                  draft.Title
                          gated
                              createPrTool
                              ([ RepoRef.value draft.Repo
                                 draft.Head
                                 draft.Base
                                 draft.Title
                                 (if draft.Draft then "true" else "false") ]
                               @ Option.toList draft.Body)
                              summary
                      MergePr =
                        fun repo number method ->
                          // The method is in the summary: a squash and a merge commit leave
                          // different histories on the base branch, and the sentence a person
                          // reads before it happens should say which.
                          gated
                              mergePrTool
                              [ RepoRef.value repo; string number; PrMergeMethod.render method ]
                              (sprintf "merge_pr %s#%d (%s)" (RepoRef.value repo) number (PrMergeMethod.render method))
                      UnmergePr =
                        fun repo number ->
                          gated
                              unmergePrTool
                              [ RepoRef.value repo; string number ]
                              (sprintf "unmerge_pr %s#%d" (RepoRef.value repo) number)
                      ReadyPr =
                        fun repo number ->
                          gated
                              readyPrTool
                              [ RepoRef.value repo; string number ]
                              (sprintf "ready_pr %s#%d" (RepoRef.value repo) number)
                      DraftPr =
                        fun repo number ->
                          gated
                              draftPrTool
                              [ RepoRef.value repo; string number ]
                              (sprintf "draft_pr %s#%d" (RepoRef.value repo) number)
                      WatchPr =
                        fun repo number ->
                          gated
                              watchPrTool
                              [ RepoRef.value repo; string number ]
                              (sprintf "watch_pr %s#%d" (RepoRef.value repo) number)
                      UnwatchPr =
                        fun repo number ->
                          gated
                              unwatchPrTool
                              [ RepoRef.value repo; string number ]
                              (sprintf "unwatch_pr %s#%d" (RepoRef.value repo) number)
                      // The READS take no gate and no approver: they change nothing, so there is
                      // nothing to approve and nothing to resume.
                      Fetch = service.FetchRepo (Repos.agentCaller turnActor)
                      // A read of pull requests, on the same credential a gated PR verb would
                      // spend — read off the same authority, so the two cannot differ.
                      ListPrs =
                        fun repo query ->
                          match services.Prs () with
                          | Some prs -> prs.List (Authority.credential (Authority.agentFor turnActor)) repo query
                          | None -> async { return Error "this session cannot read pull requests" }
                      Status = service.RepoStatus
                      Log = service.RepoLog
                      Diff = service.RepoDiff } }
        // GitHub's tools (`create_pr`, `ready_pr`, `draft_pr`, `merge_pr`, `unmerge_pr`, `watch_pr`, `unwatch_pr`) are declared
        // here, against the GATED verbs just bound above, rather than in `AgentTools.fs`
        // (Plan 16, part A cont'd): everything GitHub-specific about a session's pull
        // requests lives in `GitHubPrs.fs` and nowhere else.
        { capabilities with
            Repos = { capabilities.Repos with ProviderTools = GitHubPrs.providerTools capabilities } }

/// The turn's sandbox commands (Plan 15, stage 2) and the shell profile (Plan 25), bound to
/// the acting party.
/// A repo's sandbox is its file's to describe, so a start that names one starts it as the
/// file says, whatever the caller's declaration held.
///
/// Resolved where the gated call is MINTED, not where it is carried out, so what the gate
/// records and shows to whoever approves it is what will actually run.
///
/// A name no file declares is refused here, naming what that repo does declare — the fold
/// would otherwise start it from an empty declaration and refuse it further down for having
/// no container, which reads as a complaint about a `yession.yaml` that declares one.
///
/// It also settles WHICH sandbox a bare name means, which is the same question one step
/// earlier. `dev` is what an agent writes after reading "started sandbox octo/hello:dev",
/// and when this session holds no sandbox of its own by that name and exactly one checkout
/// declares one, there is nothing else it could mean — `WorkSandboxes.resolve` already reads
/// it that way for every verb that reaches a RUNNING sandbox. Starting was the one door that
/// did not: a bare `dev` whose repo sandbox was stopped fell through to a new session-owned
/// sandbox with none of the file's container, silently, under a name that says it is the
/// repo's. Two checkouts declaring `dev` is ambiguous and refuses, naming both.
///
/// Answers the REF as well as the declaration, because the two are one decision: a name that
/// resolved to a repo's sandbox must be started as that repo's, and the gated call has to
/// carry the resolved name or the approval shows one sandbox and the start makes another.
let private startAs
    (services: CommandServices)
    (name: SandboxRef)
    (asked: SandboxDecl)
    : Result<SandboxRef * SandboxDecl, string> =
    let declarations = services.DeclaredSandboxes ()
    let declared =
        Declarations.known declarations
        |> Map.toList
        |> List.filter (fun (ref, _) -> SandboxRef.scope ref <> SessionOwned)
    // A name missing from declarations the session could not read is a name it knows nothing
    // about, so a refusal over one says why instead of concluding the file declares nothing.
    let unlessUnread (refusal: string) =
        match Declarations.whyNotCurrent declarations with
        | None -> refusal
        | Some why -> sprintf "%s. %s" refusal why
    let asDeclared (ref: SandboxRef) (decl: SandboxDecl) = Ok (ref, decl)
    match SandboxRef.scope name with
    | SessionOwned ->
        // The session's own name wins when it has one: `start_work_sandbox "dev"` in a
        // session that already runs a `dev` is that sandbox being re-asked for, and
        // redirecting it at a repo's would answer about a sandbox nobody named.
        let held = (services.Sandboxes ()).Listed ()
        if held |> List.exists (fun entry -> entry.Ref = name) then Ok (name, asked)
        else
            match declared |> List.filter (fun (ref, _) -> SandboxRef.name ref = SandboxRef.name name) with
            | [ (ref, decl) ] -> asDeclared ref decl
            | [] -> Ok (name, asked)
            | ambiguous ->
                Error (
                    sprintf
                        "'%s' is ambiguous. %s each declare a sandbox with that name. Give the full name of the one you mean"
                        (SandboxName.value (SandboxRef.name name))
                        (ambiguous |> List.map (fst >> SandboxRef.render) |> String.concat ", "))
    | RepoOwned repo ->
        match declared |> List.tryFind (fun (ref, _) -> ref = name) with
        | Some (ref, decl) -> asDeclared ref decl
        | None ->
            let siblings =
                declared
                |> List.filter (fun (ref, _) -> SandboxRef.scope ref = RepoOwned repo)
                |> List.map (fst >> SandboxRef.render)
            match siblings, Declarations.whyNotCurrent declarations with
            // Nothing known of this repo because nothing could be read: that is the whole
            // answer, and "declares no sandboxes" would be a claim about a file nobody read.
            | [], Some why -> Error why
            | [], None ->
                Error (
                    sprintf
                        "%s declares no sandboxes. A repo's work sandbox comes from its yession.yaml. `repo_config` says what this session made of that file"
                        (RepoRef.value repo))
            | names, _ ->
                Error (
                    unlessUnread (
                        sprintf
                            "%s declares no sandbox named '%s'. It declares %s"
                            (RepoRef.value repo)
                            (SandboxName.value (SandboxRef.name name))
                            (String.concat ", " names)))

let private sandboxCapabilitiesFor
    (services: CommandServices)
    (turnActor: Principal)
    (capabilities: AgentCapabilities)
    : AgentCapabilities =
    let gated (tool: string) (args: string list) (summary: string) =
        capabilities.RunGated
            { Tool = tool
              Args = encodeArgs args
              Summary = summary
              Authority = Authority.agentFor turnActor }
    { capabilities with
        Sandboxes =
          { capabilities.Sandboxes with
              Start =
                fun name decl ->
                  match startAs services name decl with
                  | Error e -> async { return Error e }
                  | Ok (name, decl) ->
                      capabilities.RunGated (startWorkSandboxCall (Authority.agentFor turnActor) None name decl)
              Stop =
                fun name ->
                  gated
                      stopWorkSandboxTool
                      [ SandboxRef.render name ]
                      (sprintf "stop_work_sandbox %s" (SandboxRef.render name))
              SetShellProfile =
                fun name cwd -> capabilities.RunGated (setShellProfileCall (Authority.agentFor turnActor) None name cwd) } }

/// Every command verb bound to ONE turn's actor: the acting party on the events is the agent,
/// the credential is the turn human's (Plan 08). The Host leaves these as denials because
/// only the per-turn dispatcher knows who the turn is for; this is where they stop being
/// denials, and it is one call so a turn cannot pick up half of them.
/// The file commands, as gated calls. The summary is what the classifier reads and the
/// record keeps, so it names the file and the size of the change (`FileEdit.summary`) — never
/// the texts themselves, which for a whole-file write is the whole file.
let private fileCapabilitiesFor (turnActor: Principal) (capabilities: AgentCapabilities) : AgentCapabilities =
    let gated (tool: string) (args: string list) (summary: string) =
        capabilities.RunGated
            { Tool = tool
              Args = encodeArgs args
              Summary = summary
              Authority = Authority.agentFor turnActor }
    { capabilities with
        Files =
          { capabilities.Files with
              Edit =
                fun request ->
                  gated
                      editFileTool
                      [ SandboxRef.render request.Sandbox
                        request.Path
                        request.OldText
                        request.NewText
                        (if request.ReplaceAll then "true" else "false") ]
                      (FileEdit.summary request.Path request.OldText request.NewText request.ReplaceAll)
              Write =
                fun sandbox path content ->
                  gated writeFileTool [ SandboxRef.render sandbox; path; content ] (FileEdit.writeSummary path content) } }

/// Sharing a file with everyone here, as a gated call. The summary names the file and what it
/// will be called — never a version, because none exists yet: the store mints it past the
/// gate, so what is approved is "share this file under this name" and not an address that
/// could be stale by the time the verdict lands.
let private artifactCapabilitiesFor (services: CommandServices) (turnActor: Principal) (capabilities: AgentCapabilities) : AgentCapabilities =
    { capabilities with
        Artifacts =
          // Reading is NOT gated, and that is the decision rather than an omission: it reaches
          // only what has already been shared, which is to say nothing the agent could not
          // already have. A gate in front of it would ask a person to approve the agent
          // looking at the picture it put in front of them.
          { ArtifactCapabilities.Read = fun ref -> (services.Artifacts ()).Read ref
            ArtifactCapabilities.Share =
                fun sandbox path name ->
                  let summary =
                      match name with
                      | Some name -> sprintf "share_artifact %s as %s" path name
                      | None -> sprintf "share_artifact %s" path
                  capabilities.RunGated
                      { Tool = shareArtifactTool
                        Args = encodeArgs ([ SandboxRef.render sandbox; path ] @ Option.toList name)
                        Summary = summary
                        Authority = Authority.agentFor turnActor } } }

/// What a turn may do to the side pane. Not gated, and that is the decision rather than an
/// omission: opening a tab reaches nobody's filesystem, costs nothing, and undoes with one
/// tap — a gate in front of "look at this" would ask a person to approve being shown things,
/// which is the opposite of the point. What it cannot reach is anything somebody KEPT.
let private tabCapabilitiesFor (services: CommandServices) (turnActor: Principal) (capabilities: AgentCapabilities) : AgentCapabilities =
    let actor = Principal.toActor turnActor
    { capabilities with
        Tabs =
          { TabCapabilities.Open =
              fun view focus ->
                  async {
                      do! services.NoteTabOpened view focus actor
                      return Ok ()
                  }
            TabCapabilities.Close =
              fun view ->
                  async {
                      do! services.NoteTabClosed view actor
                      return Ok ()
                  } } }

let bindFor (services: CommandServices) (turnActor: Principal) (capabilities: AgentCapabilities) : AgentCapabilities =
    capabilities
    |> repoCapabilitiesFor services turnActor
    |> sandboxCapabilitiesFor services turnActor
    |> fileCapabilitiesFor turnActor
    |> artifactCapabilitiesFor services turnActor
    |> tabCapabilitiesFor services turnActor
