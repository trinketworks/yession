namespace Yession.Domain.Chat

open Yession.Domain
open Yession.Domain.Repos
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Terminals
open Yession.Domain.Tools
open Yession.Domain.Prs

/// Something a party DID on the timeline, as the FACTS of it — the event's own record, and
/// not a sentence made from one.
///
/// The fold used to build the sentence: it matched the event, `sprintf`'d a headline and a
/// detail, and stored those strings on the item. Two readers then read the strings — the
/// agent's prompt, which wants prose, and the screen, which wanted the parts and had to be
/// handed them separately (`SandboxStarted`, a per-act escape hatch on the item) whenever it
/// wanted to lay one out rather than print it. A string is the one shape that has already
/// decided how it will be read.
///
/// So an act carries its event, and the two readers are two functions over it: `phrase` and
/// `particulars` say it (the words live beside each event, in its facts file — `Act` only
/// dispatches), and the screen matches the case it knows how to arrange and phrases the
/// rest. Both fold from the same facts; neither parses the other. And the sentence stays
/// available to a person on the screen too — behind a disclosure, as what the agent was
/// told — which is the promise this shape keeps: the human can always see what the agent
/// read, and is not made to read it by default.
///
/// One case per event that is an act. Adding one is adding a case here, a `phrase` beside
/// the event, and a fold arm — and the compiler names every reader that has an opinion.
[<RequireQualifiedAccess>]
type Act =
    | RepoAdded of RepoAdded
    | RepoRemoved of RepoRemoved
    | RepoBranchSwitched of RepoBranchSwitched
    | RepoCapabilitiesChanged of RepoCapabilitiesChanged
    | RepoCapabilitiesApproved of RepoCapabilitiesApproved
    | RepoConfigRefused of RepoConfigRefused
    | SandboxStarting of WorkSandboxStarting
    | SandboxStarted of WorkSandboxStarted
    | SandboxStartFailed of WorkSandboxStartFailed
    | SandboxStopped of WorkSandboxStopped
    | SandboxSetupQueued of SandboxSetupQueued
    | ShellProfileSet of ShellProfileSet
    | FileChanged of FileChanged
    | ArtifactShared of ArtifactShared
    | CommandRefused of CommandRefused
    | GatedCommandFailed of GatedCommandFailed
    | CredentialSpent of GitCredentialSpent
    | McpServerAvailable of McpServerNoted
    | McpServerUnavailable of McpServerNoted
    | PrWatched of PrWatched
    | PrUnwatched of PrUnwatched
    | PrTransitioned of PrTransitioned

module Act =

    /// What was done, without who it was done for — the headline's first half. Dispatch
    /// only — the words are beside each event.
    let deed (act: Act) : Phrase =
        match act with
        | Act.RepoAdded r -> RepoAdded.phrase r
        | Act.RepoRemoved r -> RepoRemoved.phrase r
        | Act.RepoBranchSwitched r -> RepoBranchSwitched.phrase r
        | Act.RepoCapabilitiesChanged c -> RepoCapabilitiesChanged.phrase c
        | Act.RepoCapabilitiesApproved a -> RepoCapabilitiesApproved.phrase a
        | Act.RepoConfigRefused r -> RepoConfigRefused.phrase r
        | Act.SandboxStarting s -> WorkSandboxStarting.phrase s
        | Act.SandboxStarted s -> WorkSandboxStarted.phrase s
        | Act.SandboxStartFailed s -> WorkSandboxStartFailed.phrase s
        | Act.SandboxStopped s -> WorkSandboxStopped.phrase s
        | Act.SandboxSetupQueued q -> SandboxSetupQueued.phrase q
        | Act.ShellProfileSet p -> ShellProfileSet.phrase p
        | Act.FileChanged f -> FileChanged.phrase f
        | Act.ArtifactShared a -> ArtifactShared.phrase a
        | Act.CommandRefused c -> CommandRefused.phrase c
        | Act.GatedCommandFailed c -> GatedCommandFailed.phrase c
        | Act.CredentialSpent g -> GitCredentialSpent.deed g
        | Act.McpServerAvailable m -> McpServerNoted.available m
        | Act.McpServerUnavailable m -> McpServerNoted.unavailable m
        | Act.PrWatched p -> PrWatched.phrase p
        | Act.PrUnwatched p -> PrUnwatched.phrase p
        | Act.PrTransitioned p -> PrTransitioned.phrase p

    /// Who the act was done for, when that is not its author: " for Ada" after the deed —
    /// the person behind the agent or a repo's file, or whose credential a push spent. Empty
    /// when the author acted for themselves. Its own clause, so a narrow screen can put it
    /// on a line of its own.
    let forWhom (act: Act) : Phrase =
        let person (principal: Principal option) =
            match principal with
            | Some p -> [ Segment.Text " for "; Segment.Ref (EntityRef.Actor (Principal.toActor p)) ]
            | None -> []
        match act with
        | Act.SandboxStarting s -> person s.OnBehalfOf
        | Act.SandboxStarted s -> person s.OnBehalfOf
        | Act.SandboxStartFailed s -> person s.OnBehalfOf
        | Act.ShellProfileSet p -> person p.OnBehalfOf
        | Act.CredentialSpent g -> GitCredentialSpent.forWhom g
        | Act.RepoAdded _
        | Act.RepoRemoved _
        | Act.RepoBranchSwitched _
        | Act.RepoCapabilitiesChanged _
        | Act.RepoCapabilitiesApproved _
        | Act.RepoConfigRefused _
        | Act.SandboxStopped _
        | Act.SandboxSetupQueued _
        | Act.FileChanged _
        | Act.ArtifactShared _
        | Act.CommandRefused _
        | Act.GatedCommandFailed _
        | Act.McpServerAvailable _
        | Act.McpServerUnavailable _
        | Act.PrWatched _
        | Act.PrUnwatched _
        | Act.PrTransitioned _ -> []

    /// The person `forWhom` names, when it names one — for a reader that has to know WHO,
    /// not how to say it.
    let onBehalfOf (act: Act) : Principal option =
        match act with
        | Act.SandboxStarting s -> s.OnBehalfOf
        | Act.SandboxStarted s -> s.OnBehalfOf
        | Act.SandboxStartFailed s -> s.OnBehalfOf
        | Act.ShellProfileSet p -> p.OnBehalfOf
        | Act.CredentialSpent g -> CredentialFor.person g.Owner
        | _ -> None

    /// The headline: the one sentence a reader lands on — the deed, then who it was for.
    let phrase (act: Act) : Phrase = deed act @ forWhom act

    /// What the headline holds back, one phrase per fact. Empty is an act that is already
    /// one clause — most are: "removed repo octo/hello" has no second half to withhold, and
    /// inventing one would pad every short line into looking like a long one.
    let particulars (act: Act) : Phrase list =
        match act with
        | Act.RepoAdded r -> RepoAdded.particulars r
        | Act.RepoCapabilitiesChanged c -> RepoCapabilitiesChanged.particulars c
        | Act.RepoConfigRefused r -> RepoConfigRefused.particulars r
        | Act.SandboxStarting s -> WorkSandboxStarting.particulars s
        | Act.SandboxStarted s -> WorkSandboxStarted.particulars s
        | Act.SandboxStartFailed s -> WorkSandboxStartFailed.particulars s
        | Act.SandboxSetupQueued q -> SandboxSetupQueued.particulars q
        | Act.CommandRefused c -> CommandRefused.particulars c
        | Act.GatedCommandFailed c -> GatedCommandFailed.particulars c
        | Act.PrWatched p -> PrWatched.particulars p
        | Act.CredentialSpent g -> GitCredentialSpent.particulars g
        | Act.ArtifactShared a -> ArtifactShared.particulars a
        | Act.RepoRemoved _
        | Act.RepoBranchSwitched _
        | Act.RepoCapabilitiesApproved _
        | Act.SandboxStopped _
        | Act.ShellProfileSet _
        // The diff is not a phrase: a screen draws it as lines, and the agent already holds
        // the texts it sent. The headline's counts are the whole of what prose says.
        | Act.FileChanged _
        | Act.McpServerAvailable _
        | Act.McpServerUnavailable _
        | Act.PrUnwatched _
        | Act.PrTransitioned _ -> []

    /// The whole account as ONE sentence: headline, then the particulars after an em-dash,
    /// semicolon-joined. This is the composition every reader that is not a screen gets
    /// (`ConversationItem.said`), and the one a screen shows a person who asks what the
    /// agent was told — the same phrase, so the two cannot drift by a character. The seam
    /// is drawn here, once, rather than by each reader hunting for a punctuation mark.
    let sentence (act: Act) : Phrase =
        match particulars act with
        | [] -> phrase act
        | particulars ->
            let joined =
                particulars
                |> List.mapi (fun i p -> if i = 0 then p else Segment.Text "; " :: p)
                |> List.concat
            phrase act @ (Segment.Text " — " :: joined)

    /// How a run of work counts this act: the verb, and the noun once and many times over —
    /// "edited 1 file", "edited 3 files". A run that holds tool calls and acts together
    /// says what it holds by kind (`WorkRun.summary`), and the kind is the act's to name:
    /// a file edited and a file written are two counts, because a reader asks about them
    /// separately, while every sandbox start is one.
    let counted (act: Act) : string * string * string =
        match act with
        | Act.RepoAdded _ -> "added", "repo", "repos"
        | Act.RepoRemoved _ -> "removed", "repo", "repos"
        | Act.RepoBranchSwitched _ -> "switched", "branch", "branches"
        | Act.RepoCapabilitiesChanged _ -> "changed", "repo's capabilities", "repos' capabilities"
        | Act.RepoCapabilitiesApproved _ -> "approved", "repo's capabilities", "repos' capabilities"
        | Act.RepoConfigRefused _ -> "refused", "repo config", "repo configs"
        | Act.SandboxStarting _
        | Act.SandboxStarted _ -> "started", "sandbox", "sandboxes"
        | Act.SandboxStartFailed _ -> "failed to start", "sandbox", "sandboxes"
        | Act.SandboxStopped _ -> "stopped", "sandbox", "sandboxes"
        | Act.SandboxSetupQueued _ -> "queued", "setup", "setups"
        | Act.ShellProfileSet _ -> "set", "shell profile", "shell profiles"
        | Act.FileChanged { Change = FileChange.Edited _ } -> "edited", "file", "files"
        | Act.FileChanged { Change = FileChange.Written _ } -> "wrote", "file", "files"
        // Two counts off one event, like a file edited and a file written: a run that shared
        // three artifacts and a run that revised one three times are different stories, and a
        // reader asks about them separately.
        | Act.ArtifactShared a -> ArtifactShared.verb a, "artifact", "artifacts"
        | Act.CommandRefused _ -> "refused", "command", "commands"
        | Act.GatedCommandFailed _ -> "failed", "command", "commands"
        | Act.CredentialSpent _ -> "spent", "credential", "credentials"
        | Act.McpServerAvailable _
        | Act.McpServerUnavailable _ -> "noted", "server", "servers"
        | Act.PrWatched _ -> "watched", "pull request", "pull requests"
        | Act.PrUnwatched _ -> "unwatched", "pull request", "pull requests"
        | Act.PrTransitioned _ -> "noted", "pull request", "pull requests"

    /// Whether this act opens a chapter BY NATURE — one nobody had to ask for.
    ///
    /// A rule over the act rather than a flag set at every fold arm, for the reason the
    /// prose is: the act knows. What is notable is deliberately a short list — a transcript
    /// where everything opens a chapter has none — and `Chapters` is where a person's own
    /// verdict overrides it in either direction.
    let notable (act: Act) : bool =
        match act with
        // Where the waiting began, and the news that follows it — unlike the unwatch, which
        // is where the story stops being told rather than a place worth coming back to.
        | Act.PrWatched _
        | Act.PrTransitioned _ -> true
        // Worth seeing when it FAILED: a sandbox whose setup never ran is a sandbox the next
        // command pays for in full, and that is the case somebody should be told loudly.
        | Act.SandboxSetupQueued q -> q.Problem.IsSome
        | Act.RepoAdded _
        | Act.RepoRemoved _
        | Act.RepoBranchSwitched _
        | Act.RepoCapabilitiesChanged _
        | Act.RepoCapabilitiesApproved _
        | Act.RepoConfigRefused _
        | Act.SandboxStarting _
        | Act.SandboxStarted _
        | Act.SandboxStartFailed _
        | Act.SandboxStopped _
        | Act.ShellProfileSet _
        | Act.FileChanged _
        | Act.ArtifactShared _
        | Act.CommandRefused _
        | Act.GatedCommandFailed _
        | Act.CredentialSpent _
        | Act.McpServerAvailable _
        | Act.McpServerUnavailable _
        | Act.PrUnwatched _ -> false
