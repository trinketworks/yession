namespace Yession.Domain.Repos

open Yession.Domain

/// The session's repos, projected from events (Plan 14). The agent's git verbs and the
/// settings panel are two interfaces over one Process-side function; this projection is
/// the read model both render — deterministic, a pure function of the ordered event
/// sequence, like `EnvironmentStatus`.

type SessionRepo =
    { Repo : RepoRef
      /// The branch the checkout is on, per the last recorded add/switch. The FILESYSTEM
      /// is the truth (a terminal can move a branch without an event); this is the last
      /// state the repo manager recorded, which the manager itself reconciles on list.
      Branch : string
      /// Who brought the repo in — what the shared-trust disclosure names.
      AddedBy : ActorRef }

type ReposProjection =
    { Repos : SessionRepo list }

module ReposProjection =

    let empty : ReposProjection = { Repos = [] }

    /// Fold one event. Re-adding an existing repo replaces its entry (the clone was
    /// re-verified; the newest add wins), keeping list order stable otherwise.
    let applyEvent (proj: ReposProjection) (event: SessionEvent) : ReposProjection =
        match event with
        | RepoAdded r ->
            let entry = { Repo = r.Repo; Branch = r.Branch; AddedBy = r.Actor }
            if proj.Repos |> List.exists (fun s -> s.Repo = r.Repo) then
                { Repos = proj.Repos |> List.map (fun s -> if s.Repo = r.Repo then entry else s) }
            else
                { Repos = proj.Repos @ [ entry ] }
        | RepoRemoved r ->
            { Repos = proj.Repos |> List.filter (fun s -> s.Repo <> r.Repo) }
        | RepoBranchSwitched r ->
            { Repos =
                proj.Repos
                |> List.map (fun s -> if s.Repo = r.Repo then { s with Branch = r.Branch } else s) }
        | _ -> proj

    let tryFind (repo: RepoRef) (proj: ReposProjection) : SessionRepo option =
        proj.Repos |> List.tryFind (fun s -> s.Repo = repo)

/// One repo as the verbs and the panel report it: the FILESYSTEM's answer, not the
/// projection's — the checkout is the truth, and a listing that disagreed with `git
/// status` would teach everyone to distrust the panel.
type RepoListing =
    { Repo : RepoRef
      Branch : string
      /// Uncommitted changes present. Told, not fixed: resume drift is a fact to
      /// surface, never something a list call quietly cleans.
      Dirty : bool
      /// Where the checkout is, as a TERMINAL in this session sees it — the one fact an
      /// agent needs to do anything with a repo it just cloned, and the one nothing used
      /// to tell it. Answering "added octo/hello" and leaving the path to be guessed is
      /// how a clone turns into `ls ~/repos`, twice, and then a turn that ran out of steps.
      Path : string }

/// One repository a person could choose, as the provider lists it — enough to choose it
/// and to clone it. Provider-neutral by construction: a forge's listing is decoded into
/// this in the session, and the browser's picker reads only this.
///
/// The name is the one the PROVIDER calls canonical, which is the fact a clone cannot
/// learn: github.com follows a renamed repository's old name with a redirect, so a
/// checkout made from one clones fine and keeps an `origin` the provider no longer answers
/// to by that name. A repo chosen from a listing was named by the provider.
[<RequireQualifiedAccess>]
type RepoCandidate =
    { Repo : RepoRef
      Description : string option
      DefaultBranch : string
      /// Whether the credential is what makes it visible: a private repo chosen here will
      /// not clone for a session whose credential cannot see it.
      Private : bool
      /// When it was last pushed to, as the provider reports it — what "recent" is ordered by.
      PushedAt : string option }

module RepoListing =

    /// Render one listing line the way both interfaces say it.
    let describe (listing: RepoListing) : string =
        sprintf
            "%s (branch %s%s) at %s"
            (RepoRef.value listing.Repo)
            listing.Branch
            (if listing.Dirty then ", uncommitted changes" else "")
            listing.Path

/// What a pasted link, or a typed name, asks for: a repository, one of its branches, or a
/// pull request — the three things a person copies out of a forge's address bar.
///
/// A link is read here rather than sent to the provider to resolve, because two of the
/// three need no provider at all: `owner/name` is already what `add_repo` takes, and a
/// branch link is that plus a switch. Only a pull request has to be asked about, since
/// which repository and branch it comes FROM is a fact only the provider holds — a fork's
/// as often as not.
[<RequireQualifiedAccess>]
type RepoLink =
    | Repo of RepoRef
    | Branch of RepoRef * branch: string
    | PullRequest of RepoRef * number: int

module RepoLink =

    /// The hosts a link is recognised under. github.com is the one this session clones from
    /// (`RepoRef.cloneUrl`), so a link anywhere else is not a repo link, whatever its path.
    let private hosts = [ "github.com"; "www.github.com" ]

    let private stripPrefix (prefix: string) (text: string) : string option =
        if text.StartsWith prefix then Some (text.Substring prefix.Length) else None

    /// The path under the host, when `text` is a URL to one of `hosts`: `https://`,
    /// `http://`, or bare — because a URL copied from a browser has a scheme and one read
    /// off a page often does not.
    let private hostedPath (text: string) : string option =
        let unschemed =
            stripPrefix "https://" text
            |> Option.orElse (stripPrefix "http://" text)
            |> Option.defaultValue text
        hosts
        |> List.tryPick (fun host -> stripPrefix (host + "/") unschemed)

    /// A path's tail with the query and fragment gone — a link to a pull request's files
    /// tab, or a branch link with a search in it, is still a link to that thing.
    let private untailed (text: string) : string =
        let cut (c: char) (s: string) =
            match s.IndexOf c with
            | -1 -> s
            | i -> s.Substring (0, i)
        text |> cut '?' |> cut '#'

    /// Read what a link asks for, or nothing when it is not one. The forms:
    ///
    ///   owner/name                       owner/name.git
    ///   github.com/owner/name            https://github.com/owner/name/
    ///   git@github.com:owner/name.git
    ///   https://github.com/owner/name/tree/<branch>      branches carry `/`
    ///   https://github.com/owner/name/pull/<n>           and anything after the number
    ///
    /// A `blob` link is deliberately not one: `blob/main/src/x.fs` does not say where the
    /// branch ends and the path begins, and GitHub itself only knows by asking. A link that
    /// names a branch this way is pasted as a `tree` link instead, which every branch page
    /// offers.
    let parse (text: string) : RepoLink option =
        let trimmed = if isNull text then "" else text.Trim ()
        let repoOf (segment: string) = RepoRef.create segment |> Result.toOption
        let segments =
            match stripPrefix "git@github.com:" trimmed with
            | Some rest -> Some (untailed rest)
            | None -> hostedPath trimmed |> Option.map untailed |> Option.orElse (Some trimmed)
        match segments |> Option.map (fun s -> s.TrimEnd('/').Split '/' |> Array.toList) with
        | Some [ owner; name ] -> repoOf (owner + "/" + name) |> Option.map RepoLink.Repo
        | Some (owner :: name :: "tree" :: branch) when not branch.IsEmpty ->
            repoOf (owner + "/" + name) |> Option.map (fun repo -> RepoLink.Branch (repo, String.concat "/" branch))
        | Some (owner :: name :: "pull" :: number :: _) ->
            match System.Int32.TryParse number with
            | true, n when n > 0 -> repoOf (owner + "/" + name) |> Option.map (fun repo -> RepoLink.PullRequest (repo, n))
            | _ -> None
        | _ -> None

/// Where a pull request comes FROM, as the provider says: the repository holding its head
/// branch — the fork, when it is one — and that branch. What a link to a pull request
/// becomes once asked about, and all `add_repo` needs to check it out.
[<RequireQualifiedAccess>]
type PullHead =
    { Repo : RepoRef
      Branch : string }
