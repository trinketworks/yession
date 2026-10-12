namespace Yession.Session

open Yession.Domain
open Yession.Domain.Artifacts
open Yession.Domain.Collab
open Yession.App.Collab

/// Files attached to messages, as the session acts on them. The rules are
/// `Yession.Domain.Collab.Attachments`; this is where they meet the doc and the staging store.
module StagedAttachments =

    /// The staging store, as a mechanism: it holds bytes by upload id and answers what it
    /// holds. It decides nothing — when a file is shared and when one is deleted are the
    /// attachment rules', applied by the drain and by `sweep`.
    type Staging =
        { /// Whether an upload's bytes have arrived whole.
          Arrived : UploadId -> bool
          /// Share an arrived upload as the next version of `name`, recorded as the actor's.
          /// The staged copy stays where it is: it goes when `sweep` finds nothing wants it,
          /// so a drain that dies before its `MessageSent` can share it again.
          Share : ActorRef -> string -> UploadId -> Async<Result<ArtifactShared, string>>
          /// Every staged upload, with when it landed.
          Listed : unit -> (UploadId * System.DateTimeOffset) list
          /// Delete a staged upload.
          Discard : UploadId -> unit }

    /// A session with nowhere to stage: nothing has arrived, so a message with an attachment
    /// waits, and there is nothing to sweep.
    let none : Staging =
        { Arrived = fun _ -> false
          Share = fun _ _ _ -> async { return Error "this session has nowhere to keep artifacts" }
          Listed = fun () -> []
          Discard = ignore }

    /// Apply the attachment rules to what is there now: entries nothing can send leave the
    /// doc, and staged files nothing wants leave the disk once `Attachments.grace` has passed.
    let sweep (doc: Yjs.Y.Doc) (staging: Staging) (now: System.DateTimeOffset) : unit =
        let synced = SyncedStateSync.ofDoc doc
        SyncedStateSync.removeAttachments doc (Attachments.dead synced)
        Attachments.orphans now synced (staging.Listed ()) |> List.iter staging.Discard

    /// Share one message's attachments, in the order `Attachments.ofMessage` gives, and answer
    /// with what the message says about each: its pinned address, or why it is not there.
    /// A file that cannot be shared does not hold the message — the person who sent it reads
    /// the reason in what was sent, rather than watching the queue stop for no visible cause.
    let share
        (staging: Staging)
        (actorFor: PeerId -> Principal)
        (attachments: Map<UploadId, Attachment>)
        (queueId: QueueId)
        : Async<string list> =
        async {
            let said = ResizeArray ()
            for attachment in Attachments.ofMessage queueId attachments do
                let actor = Principal.toActor (actorFor attachment.Uploader)
                match! staging.Share actor attachment.Name attachment.Upload with
                | Ok shared -> said.Add (Content.ArtifactRef.url shared.Ref)
                | Error why -> said.Add (sprintf "(%s could not be attached: %s)" attachment.Name why)
            return List.ofSeq said
        }
