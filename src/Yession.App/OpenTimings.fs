namespace Yession.App

/// A moment in a page's opening of its session, from the navigation that began it to the
/// timeline on the screen. Closed, so the set of names a page's opening is told in is this
/// type. Each is reached once on a page load: an opening is the first time through, and a
/// link lost and found again afterwards is not one.
///
/// They are not a sequence. A page that kept a copy of its session shows it (`Shown`) before
/// the session has answered at all, and one that kept nothing shows the timeline only once
/// the log is read through. Which came first is what a reading of them is for.
[<RequireQualifiedAccess>]
type OpenMark =
    /// The app's own code began to run: the document, the stylesheet and the bundle have
    /// arrived and been evaluated.
    | Booted
    /// This device's store said what it kept of the session (`LocalCopy` left `Reading`),
    /// or the page learned it has no store to ask.
    | StoreAnswered
    /// The session said who this page is signed in as (the `me` probe answered).
    | Authorized
    /// The data channel to the session opened.
    | ChannelOpen
    /// The session took this page, and said where its log ends (`SessionLink.live`).
    | Accepted
    /// Everything the session said its log holds is folded (`Phase.ReadThrough`).
    | ReadThrough
    /// A frame has been drawn with the timeline showing what is folded
    /// (`ClientModel.showsFolded`): the reader's own kept copy, or the log read through.
    | Shown

[<RequireQualifiedAccess>]
module OpenMark =

    let all : OpenMark list =
        [ OpenMark.Booted
          OpenMark.StoreAnswered
          OpenMark.Authorized
          OpenMark.ChannelOpen
          OpenMark.Accepted
          OpenMark.ReadThrough
          OpenMark.Shown ]

    /// The mark's name wherever it is written down: in the browser's own timeline, and in
    /// what a page reports of its opening.
    let name (mark: OpenMark) : string =
        match mark with
        | OpenMark.Booted -> "booted"
        | OpenMark.StoreAnswered -> "store_answered"
        | OpenMark.Authorized -> "authorized"
        | OpenMark.ChannelOpen -> "channel_open"
        | OpenMark.Accepted -> "accepted"
        | OpenMark.ReadThrough -> "read_through"
        | OpenMark.Shown -> "shown"

/// When each moment of a page's opening was reached, in milliseconds since the navigation
/// began (the browser's `performance.now()`). Pure: the page hands it the clock's reading and
/// the marks reached, and it answers the ones that are news.
[<RequireQualifiedAccess>]
module OpenTimings =

    /// What the browser's own timeline (`performance.mark`) names a mark: this, then
    /// `OpenMark.name`. The whole opening, navigation to `Shown`, is a measure named `measure`.
    let markPrefix = "yession:open:"

    let measure = "yession:open"

    /// The marks reached so far, newest first.
    type State = private { Reached : (OpenMark * float) list }

    let empty : State = { Reached = [] }

    /// Every mark reached, with when, in the order they were reached.
    let reached (state: State) : (OpenMark * float) list = List.rev state.Reached

    /// The marks a model says have been reached. `Booted`, `Authorized`, `ChannelOpen` and
    /// `Shown` are not the model's to say: the page reaches those where they happen.
    let ofModel (model: ClientModel) : OpenMark list =
        [ match model.LocalCopy with
          | LocalCopy.Reading -> ()
          | LocalCopy.Kept _
          | LocalCopy.NoneKept
          | LocalCopy.Unkeepable
          | LocalCopy.Unread _ -> OpenMark.StoreAnswered
          if SessionLink.live model.Link then OpenMark.Accepted
          match SessionLink.phase model.Link with
          | Phase.Opening -> ()
          | Phase.ReadThrough -> OpenMark.ReadThrough ]

    /// These marks stand reached at `at`. Answers the ones reached for the first time, which
    /// is what there is to write down: a mark reached before keeps the time it was first
    /// reached at.
    let step (state: State) (at: float) (marks: OpenMark list) : State * (OpenMark * float) list =
        let fresh =
            marks
            |> List.distinct
            |> List.filter (fun mark -> state.Reached |> List.forall (fun (seen, _) -> seen <> mark))
            |> List.map (fun mark -> mark, at)
        { Reached = List.rev fresh @ state.Reached }, fresh
