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
      /// The answer as the person read it, one line per answer: an option's label rather than
      /// its value, never JSON (`A2ui.Project.responses`). Kept on the event because the surface
      /// is usually gone by the time anyone reads the line back.
      Responses : string list
      By : Principal }

module SurfaceActionTaken =

    /// "responded main" for one answer, "responded with 3 answers" for several, which a screen
    /// unfolds into the list (`particulars`).
    let phrase (a: SurfaceActionTaken) : Phrase =
        match a.Responses with
        | [] -> Phrase.deed "responded" []
        | [ one ] -> Phrase.deed "responded" [ Segment.Text one ]
        | many -> Phrase.deed "responded" [ Segment.Text (sprintf "with %d answers" many.Length) ]

    /// The answers when there are several, then what the agent needs to act on them exactly:
    /// the event and the values the button sent, which are the agent's own words for them.
    let particulars (a: SurfaceActionTaken) : Phrase list =
        let answers =
            match a.Responses with
            | []
            | [ _ ] -> []
            | many -> many |> List.map Phrase.text
        let sent =
            if a.Context = "{}" then sprintf "event %s on surface %s" a.Name a.SurfaceId
            else sprintf "event %s on surface %s with context %s" a.Name a.SurfaceId a.Context
        answers @ [ Phrase.text sent ]

/// Somebody took down a surface the agent showed without answering it. Every surface can be
/// dismissed, whatever buttons the agent drew, because a question nobody wants to answer must
/// not hold the dock until the agent next runs.
///
/// A reply like a press is, so it is on the log and owes the agent a turn (`SurfaceAnswered`):
/// the agent asked something and has to learn it will not get the answer. The surface is gone
/// by the time anyone reads the line back, so what it was about is kept here.
[<RequireQualifiedAccess>]
type SurfaceDismissed =
    { MessageId : MessageId
      SurfaceId : string
      /// The surface's first line of text as people saw it (`A2ui.Project.title`), when it had one.
      Title : string option
      By : Principal }

module SurfaceDismissed =

    /// "dismissed “Which branch?”", or the surface's id when it said nothing.
    let phrase (d: SurfaceDismissed) : Phrase =
        match d.Title with
        | Some title -> Phrase.deed "dismissed" [ Segment.Text (sprintf "“%s”" title) ]
        | None -> Phrase.deed "dismissed" [ Segment.Text (sprintf "surface %s" d.SurfaceId) ]

    /// The surface's id, which is the agent's name for it, when the headline used its title.
    let particulars (d: SurfaceDismissed) : Phrase list =
        match d.Title with
        | Some _ -> [ Phrase.text (sprintf "surface %s, now taken down" d.SurfaceId) ]
        | None -> [ Phrase.text "now taken down" ]
