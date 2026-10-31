namespace Yession.Domain.Surfaces

open Yession.Domain

/// Somebody pressed a button on a surface the agent showed (`show_surface`): the A2UI spec's
/// `action`, as the log keeps it.
///
/// The log rather than the doc, because an answer is a thing that HAPPENED. The surface is
/// live state the agent may take down a moment later; who answered what is what a later turn,
/// and a person scrolling back, needs to read. And the log is what wakes the agent
/// (`AgentWake`), so the answer and the wake are one fact rather than two that could disagree.
///
/// The context is the spec's: the values the button named, read from the data model at the
/// moment it was pressed, by the Session rather than the browser — so what is recorded is what
/// the doc said, not what a peer claimed it said.
[<RequireQualifiedAccess>]
type SurfaceActionTaken =
    { MessageId : MessageId
      SurfaceId : string
      /// The button's component id.
      Component : string
      /// The event name the agent gave the button.
      Name : string
      /// The event's context as JSON object text. Text, because the domain names no JSON
      /// library, and the reader of it is the agent, which reads JSON.
      Context : string
      By : Principal }

module SurfaceActionTaken =

    let phrase (a: SurfaceActionTaken) : Phrase =
        Phrase.deed "pressed" [ Segment.Text (sprintf "%s on surface %s" a.Name a.SurfaceId) ]

    /// What the button carried, which is the answer. Left out when it carried nothing: "{}"
    /// says nobody anything.
    let particulars (a: SurfaceActionTaken) : Phrase list =
        if a.Context = "{}" then [] else [ Phrase.text a.Context ]
