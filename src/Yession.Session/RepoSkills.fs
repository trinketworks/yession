namespace Yession.Session

open Yession.Domain
open Yession.Domain.Repos

/// The skills that each checkout holds, read when a turn starts and recorded when a list
/// changed.
///
/// A repository can make its skills when its sandbox starts (a package manager installs
/// them), and no commit holds those. So the list is not a fact of the clone. It is what the
/// checkout holds now, and the session asks when a turn starts (`Scheduler`). The answer is a
/// record only when it differs from the last one, so the menu stays a projection of the log
/// (`ReposProjection`) and a turn that found nothing new adds no event.
[<RequireQualifiedAccess>]
module RepoSkills =

    /// One repo whose list is not the list recorded last: the list read now, and the names
    /// that came and went.
    type Change =
        { Repo : RepoRef
          Skills : SkillSummary list
          Added : string list
          Removed : string list }

    /// The names of the skills that `now` holds and `last` does not, and of those that `last`
    /// holds and `now` does not. A skill is its file here: the same name in another
    /// directory is another skill.
    let difference (last: SkillSummary list) (now: SkillSummary list) : string list * string list =
        let namesNotIn (others: SkillSummary list) (skills: SkillSummary list) : string list =
            skills
            |> List.filter (fun skill -> others |> List.forall (fun other -> other.Path <> skill.Path))
            |> List.map (fun skill -> skill.Name)
            |> List.sort
        namesNotIn last now, namesNotIn now last

    /// The repos whose list, as read now, is not the list recorded last. A repo with no
    /// answer keeps its last list, and so is not here.
    let changed (repos: SessionRepo list) (read: (RepoRef * SkillSummary list) list) : Change list =
        read
        |> List.choose (fun (repo, skills) ->
            repos
            |> List.tryFind (fun known -> known.Repo = repo && known.Skills <> skills)
            |> Option.map (fun known ->
                let added, removed = difference known.Skills skills
                { Repo = repo; Skills = skills; Added = added; Removed = removed }))

    /// Read the skills of each repo and record each list that changed.
    ///
    /// `repos` is the projection that the caller folded from the log, so this reads no log of
    /// its own. `read` is the port to the checkout (`ReposService.SkillsOf`), and it has a
    /// deadline of its own. This reads the repos at the same time, so the whole read takes as
    /// long as its slowest checkout and not as long as all of them. A read that fails, or
    /// that throws, leaves the last list as it is and writes nothing: the menu is worth less
    /// than the turn that waits for it.
    let refresh
        (log: EventLog<SessionEvent>)
        (mintMessageId: unit -> MessageId)
        (read: RepoRef -> Async<Result<SkillSummary list, string>>)
        (repos: SessionRepo list)
        : Async<unit> =
        let asked (repo: SessionRepo) : Async<(RepoRef * SkillSummary list) option> =
            async {
                match! Async.Catch (read repo.Repo) with
                | Choice1Of2 (Ok skills) -> return Some (repo.Repo, skills)
                | Choice1Of2 (Error _)
                | Choice2Of2 _ -> return None
            }
        async {
            if not (List.isEmpty repos) then
                let! answers = repos |> List.map asked |> Async.Parallel
                for change in changed repos (answers |> Array.choose id |> List.ofArray) do
                    // `ActorRef.System`, because nobody in the session did this.
                    let! _ =
                        log.Append
                            ActorRef.System
                            (SessionEvent.RepoSkillsChanged
                                { RepoSkillsChanged.MessageId = mintMessageId ()
                                  RepoSkillsChanged.Repo = change.Repo
                                  RepoSkillsChanged.Skills = change.Skills
                                  RepoSkillsChanged.Added = change.Added
                                  RepoSkillsChanged.Removed = change.Removed })
                    ()
        }
