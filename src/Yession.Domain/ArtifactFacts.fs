namespace Yession.Domain.Artifacts

open Yession.Domain
open Yession.Domain.Content

/// An artifact version put into the session — the act on the timeline.
///
/// The bytes are NOT here and never will be: the log is what a reader replays from cold, and an
/// image in it would be paid for on every fold. What is here is everything a reader can answer
/// a question with — which version, how big, what type, whose — and the address the bytes are at
/// (`ArtifactRef.content`), which is stable because an artifact version is immutable.
///
/// One event for both readings. A version's number already says which it is — 0 put the name
/// there, anything higher succeeded a version somebody could have been looking at — so a second
/// event case would be a fact that could contradict the ref beside it. `ArtifactShared.verb`
/// is where "shared" and "updated" are said, off the ref.
[<RequireQualifiedAccess>]
type ArtifactShared =
    { MessageId : MessageId
      /// Which version of which artifact. Immutable, so this doubles as the address.
      Ref : ArtifactRef
      /// The type the store settled on, `None` when it is not one this build shows — which is a
      /// download, not a guess (`ContentKind`).
      MediaType : string option
      Bytes : int64
      Digest : ContentDigest
      Actor : ActorRef }

module ArtifactShared =

    /// What the act DID, in one word: the first version of a name is shared, a later one is an
    /// update of something a reader may already have seen.
    let verb (a: ArtifactShared) : string = if ArtifactRef.isFirst a.Ref then "shared" else "updated"

    /// The headline a reader lands on. The artifact is a REFERENCE rather than words, so the
    /// chat draws the same chip it draws for a repo or a sandbox, and the agent's prose gets the
    /// `file:///` address it can quote straight back into a tool.
    let phrase (a: ArtifactShared) : Phrase =
        [ Segment.Text (verb a + " artifact ")
          Segment.Ref (EntityRef.Content (ArtifactRef.content a.Ref))
          Segment.Text (sprintf " (%s)" (ContentSize.render a.Bytes)) ]

    /// What the headline holds back: which version this is, and only when it is not the first —
    /// "version 1" said of a first share is a fact nobody was wondering about. The old versions
    /// are still there and still addressable, which is the thing a reader of an update wants to
    /// know, and the number is how they ask for one.
    let particulars (a: ArtifactShared) : Phrase list =
        if ArtifactRef.isFirst a.Ref then []
        else [ Phrase.text (sprintf "version %d" (ArtifactRef.seq a.Ref + 1)) ]
