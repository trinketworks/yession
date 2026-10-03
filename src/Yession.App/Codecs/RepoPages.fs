namespace Yession.App.Codecs

open Yession.Domain
open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open System.Globalization
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The pages the App walks when it lists what a person could open: their repositories, a
/// repository's branches, and the head of a pull request. The Session fetches each page from
/// GitHub and answers in this shape; the App owns it.
[<RequireQualifiedAccess>]
module RepoPages =

    let private candidate : Codec<Repos.RepoCandidate> =
        { Encode =
            fun (candidate: Repos.RepoCandidate) ->
                Encode.object
                    [ "repo", Codec.repoRef.Encode candidate.Repo
                      "description", Encode.option Encode.string candidate.Description
                      "defaultBranch", Encode.string candidate.DefaultBranch
                      "private", Encode.bool candidate.Private
                      "pushedAt", Encode.option Encode.string candidate.PushedAt ]
          Decode =
            Decode.object (fun get ->
                { Repos.RepoCandidate.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  Repos.RepoCandidate.Description = get.Optional.Field "description" Decode.string
                  Repos.RepoCandidate.DefaultBranch = get.Required.Field "defaultBranch" Decode.string
                  Repos.RepoCandidate.Private = get.Optional.Field "private" Decode.bool |> Option.defaultValue false
                  Repos.RepoCandidate.PushedAt = get.Optional.Field "pushedAt" Decode.string }) }

    /// What a person chooses a repo FROM, as the session serves it to the picker — the
    /// `modelCatalogue` shape, for its reason: an object around the list, with room to grow.
    ///
    /// `next` is absent at the end of the listing rather than null-and-present: the picker
    /// asks whether there is one, and an optional field answers that in the codec rather
    /// than in a reader downstream.
    let repos : Codec<Repos.RepoPage> =
        { Encode =
            fun (page: Repos.RepoPage) ->
                Encode.object
                    [ yield "repos", Encode.list (page.Candidates |> List.map candidate.Encode)
                      match page.Next with
                      | Some next -> yield "next", Encode.string next
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { Repos.RepoPage.Candidates = get.Required.Field "repos" (Decode.list candidate.Decode)
                  Repos.RepoPage.Next = get.Optional.Field "next" Decode.string }) }

    /// One page of a repo's branches, by name — `repoPage`'s shape, since it is the same
    /// question asked of a different listing.
    let branches : Codec<Repos.BranchPage> =
        { Encode =
            fun (page: Repos.BranchPage) ->
                Encode.object
                    [ yield "branches", Encode.list (page.Names |> List.map Encode.string)
                      match page.Next with
                      | Some next -> yield "next", Encode.string next
                      | None -> () ]
          Decode =
            Decode.object (fun get ->
                { Repos.BranchPage.Names = get.Required.Field "branches" (Decode.list Decode.string)
                  Repos.BranchPage.Next = get.Optional.Field "next" Decode.string }) }

    /// Where a pull request comes from: the repository holding its head, and the branch.
    let pullHead : Codec<Repos.PullHead> =
        { Encode =
            fun (head: Repos.PullHead) ->
                Encode.object [ "repo", Codec.repoRef.Encode head.Repo; "branch", Encode.string head.Branch ]
          Decode =
            Decode.object (fun get ->
                { Repos.PullHead.Repo = get.Required.Field "repo" Codec.repoRef.Decode
                  Repos.PullHead.Branch = get.Required.Field "branch" Decode.string }) }
