namespace Yession.App

open Thoth.Json

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
    /// The first byte of the document arrived, as the browser timed it: the navigation, any
    /// sign-in bounce it went through, and the session's answer are behind the page.
    | Responded
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
        [ OpenMark.Responded
          OpenMark.Booted
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
        | OpenMark.Responded -> "responded"
        | OpenMark.Booted -> "booted"
        | OpenMark.StoreAnswered -> "store_answered"
        | OpenMark.Authorized -> "authorized"
        | OpenMark.ChannelOpen -> "channel_open"
        | OpenMark.Accepted -> "accepted"
        | OpenMark.ReadThrough -> "read_through"
        | OpenMark.Shown -> "shown"

    /// The mark a name is `name` of.
    let ofName (named: string) : OpenMark option = all |> List.tryFind (fun mark -> name mark = named)

/// What a page tells its session of its own opening: when each moment was reached, and when
/// the page said so, all in milliseconds since the navigation began. No clock of the page's
/// crosses but that one: the session places the opening in time by when the report arrives.
type OpenReport =
    { Marks : (OpenMark * float) list
      SentAt : float }

/// The report's wire form: one codec, written by the page and read by the session.
///
/// The session reads it from anybody who can reach it with a cookie, so the reading refuses
/// what a page of this build would not write: a mark it does not know, a mark said twice, a
/// time that is not a time, or one outside the opening the report says it is of.
[<RequireQualifiedAccess>]
module OpenReport =

    /// The longest opening a report can be of. A page still opening after this has a fault
    /// to report, not a duration.
    let longestMs = 3_600_000.0

    /// The most a report's body can weigh on the wire, which a report of every mark is well
    /// under.
    let largestBytes = 2048

    let private encoder (report: OpenReport) =
        Encode.object
            [ "marks",
              report.Marks
              |> List.map (fun (mark, at) -> Encode.object [ "mark", Encode.string (OpenMark.name mark); "at", Encode.float at ])
              |> Encode.list
              "sentAt", Encode.float report.SentAt ]

    let private mark : Decoder<OpenMark * float> =
        Decode.object (fun get -> get.Required.Field "mark" Decode.string, get.Required.Field "at" Decode.float)
        |> Decode.andThen (fun (named, at) ->
            match OpenMark.ofName named with
            | Some mark -> Decode.succeed (mark, at)
            | None -> Decode.fail (sprintf "%s is no moment of an opening" named))

    let private decoder : Decoder<OpenReport> =
        Decode.object (fun get ->
            { Marks = get.Required.Field "marks" (Decode.list mark)
              SentAt = get.Required.Field "sentAt" Decode.float })
        |> Decode.andThen (fun report ->
            let within (at: float) = at >= 0.0 && at <= report.SentAt
            if not (report.SentAt >= 0.0 && report.SentAt <= longestMs) then
                Decode.fail "the report was not sent within an opening"
            elif List.isEmpty report.Marks then Decode.fail "the report is of no moment"
            elif report.Marks |> List.exists (fun (_, at) -> not (within at)) then
                Decode.fail "a moment is outside the opening it is reported in"
            elif (report.Marks |> List.distinctBy fst |> List.length) <> List.length report.Marks then
                Decode.fail "a moment is reported twice"
            else Decode.succeed report)

    let toJson (report: OpenReport) : string = report |> encoder |> Encode.toString 0

    let ofJson (json: string) : Result<OpenReport, string> = Decode.fromString decoder json

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

    /// Whether the opening is over: the timeline is on the screen and the log is read
    /// through. What a page waits for before it reports, so the report is of the whole of it.
    let complete (state: State) : bool =
        [ OpenMark.Shown; OpenMark.ReadThrough ]
        |> List.forall (fun mark -> state.Reached |> List.exists (fun (seen, _) -> seen = mark))

    /// What there is to report at `sentAt`, or nothing when no moment has been reached.
    let report (state: State) (sentAt: float) : OpenReport option =
        match reached state with
        | [] -> None
        | marks -> Some { Marks = marks; SentAt = sentAt }

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
