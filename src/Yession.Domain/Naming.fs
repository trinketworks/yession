namespace Yession.Domain.Chat

open Yession.Domain
open Yession.Domain.Agent

/// What the session owes in the way of names (Plan 25).
///
/// A chapter is made wearing a guess — the first line of the message it opens at — and a few
/// words from a model replace that guess. This is the rule that decides WHICH subjects want
/// naming right now, and it is one fold over facts the session already holds rather than a
/// test each caller writes: the guard it carries is the only thing between a model and a name
/// somebody typed themselves, and a second copy of it would be the copy that got it wrong.
///
/// Its memory is the event log. Every question a pass asks is about the past — may this still
/// be written over, is the asking over, and has enough been said since to be worth asking
/// again — so the `SessionNamed` facts answer all three, and a restarted process picks up
/// exactly where it left off instead of either re-asking everything or refusing to ask
/// anything.
module Naming =

    /// One subject wanting a name, and everything the asking needs.
    type Job =
        { Subject : NamingSubject
          Ask : SummaryAsk
          /// What the name reads NOW: what the write must still find there to be allowed to
          /// replace it. A second passes while a model thinks, and somebody typing in that
          /// second has named this themselves.
          Held : string
          /// How much this ask reads, to be recorded with the answer. Carried on the job
          /// rather than counted again afterwards, so the number the fact stores is the
          /// number the ask actually covered.
          Read : int }

    /// Where a subject stands after everything the session has settled about it.
    type Settled =
        { /// What the last pass left the name reading.
          Name : string
          /// How much that pass covered.
          Read : int
          /// The pass left the name where it found it. Every ask after the first hands the
          /// model the standing name and asks it to keep those words or better them, so an
          /// answer identical to what it was given is the model saying this name is the one —
          /// and that FINISHES the subject, because a name a reader has started using is
          /// worth more than a name that is marginally more apt.
          ///
          /// Derived from consecutive facts rather than recorded on one, so it cannot
          /// disagree with the log it came from and needs nothing of already-written events.
          Kept : bool }

    /// Fold one event into what the session has settled so far.
    let applyEvent (acc: Map<NamingSubject, Settled>) (event: SessionEvent) : Map<NamingSubject, Settled> =
        match event with
        | SessionNamed named ->
            let kept =
                match Map.tryFind named.Subject acc with
                | Some before -> before.Name = named.Name
                | None -> false
            Map.add named.Subject { Name = named.Name; Read = named.Read; Kept = kept } acc
        | _ -> acc

    let ofEvents (events: SessionEvent list) : Map<NamingSubject, Settled> =
        events |> List.fold applyEvent Map.empty

    /// Whether the session may still write this name, given what it last settled it to.
    ///
    /// Two ways to be allowed and one to be finished. The guess is writable because nobody
    /// chose it; the session's own last answer is writable because it wrote it. Anything else
    /// is a person's words, and a person's words end the question for good — there is no
    /// re-asking a subject somebody has named, however much is said afterwards.
    let private ours (settled: Settled option) (held: string) : bool =
        match settled with
        | Some last -> held = last.Name
        | None -> false

    /// Whether this subject is done being asked about, however much is said from here on.
    ///
    /// Stability over aptness, deliberately. A name is a REFERENCE — somebody has it in a
    /// list, in a tab, in their head — and one that keeps improving under them costs more
    /// than the improvement is worth. So the naming of a subject is a thing that finishes,
    /// and the two ways it finishes are the model saying so and the reading running out.
    ///
    /// The second is not a spare for the first. `Kept` is what ends it properly, and it is
    /// the model's judgement rather than a count — which is what lets a session that opened
    /// with "clone z" and put the work in the next message still be named for the work. But
    /// a model handed the same material twice can answer differently twice, and an ask that
    /// has run out of NEW material to read cannot be asking a new question: past this much,
    /// every ask sends the same lines and the same standing name as the one before it. So
    /// the second is what stops an unlucky session asking forever, and it is where the
    /// asking would have stopped mattering anyway.
    let private finished (settled: Settled option) (bound: int) : bool =
        match settled with
        | Some last -> last.Kept || last.Read >= bound
        | None -> false

    /// Whether enough has been said since the last ask to be worth asking again.
    ///
    /// Doubling rather than any growth at all. A chapter's stretch grows with every message
    /// until the next chapter opens, so "ask when anything is new" is one model call per
    /// message for as long as the session lasts; doubling makes it a handful over a whole
    /// session. And it still catches the case the rule is for — a session that opened with
    /// "run tests" and put the actual work in the second message has doubled by the time that
    /// message lands.
    let private worthAsking (settled: Settled option) (covered: int) : bool =
        let read = settled |> Option.map (fun last -> last.Read) |> Option.defaultValue 0
        covered >= max 1 (read * 2)

    /// Everything wanting a name right now, and what to ask for each.
    ///
    /// Deliberately silent about whether anything CAN write: what is owed is true whether or
    /// not there is a credential to spend on it, and a session that connects one an hour in
    /// then owes exactly this and names all of it at once.
    ///
    /// A chapter an act opens by NATURE has no entry in the doc until somebody touches it, so
    /// it is not a subject here and is never named. That is the right line rather than an
    /// oversight — an act note is a sentence somebody already wrote short ("PR octo/hello#12
    /// merged"), and what this names is where a person divided the session and left the guess
    /// standing.
    /// The session's own name, when it wants one.
    ///
    /// A title has no guess, so the state nobody chose is the empty one — which makes the
    /// ownership rule read the same as a chapter's with one fewer way to be writable, and
    /// means a session somebody titled by hand before anything was said is theirs from the
    /// start.
    let private titleOwed
        (settled: Map<NamingSubject, Settled>)
        (title: string)
        (items: ConversationItem list)
        : Job option =
        let last = Map.tryFind NamingSubject.Title settled
        if not (title = "" || ours last title) then None
        elif finished last Titles.ReadItems then None
        elif not (worthAsking last (List.length items)) then None
        else
            Some
                { Subject = NamingSubject.Title
                  Ask = Titles.summaryAsk items (last |> Option.map (fun fact -> fact.Name))
                  Held = title
                  Read = List.length items }

    let owed
        (settled: Map<NamingSubject, Settled>)
        (title: string)
        (chapters: Map<MessageId, ChapterMark>)
        (items: ConversationItem list)
        : Job list =
        let chapterJobs =
            chapters
            |> Map.toList
            |> List.filter (fun (_, mark) -> mark.Opens)
            |> List.choose (fun (messageId, _) ->
                items |> List.tryFind (fun item -> item.MessageId = messageId))
            |> List.choose (fun item ->
                let subject = NamingSubject.Chapter item.MessageId
                let last = Map.tryFind subject settled
                let held = Chapters.name chapters item
                if not (Chapters.unwritten chapters item || ours last held) then None
                elif finished last Chapters.ReadItems then None
                else
                    let covered = Chapters.covers chapters items item
                    if not (worthAsking last (List.length covered)) then None
                    else
                        Some
                            { Subject = subject
                              // What it is called ALREADY is the session's own last answer,
                              // not whatever the doc happens to read: on a first ask there is
                              // nothing to keep, and the guess is not a name anybody chose —
                              // handing it over would be asking a model to reword the first
                              // line of a message rather than to name what the part is about.
                              Ask = Chapters.summaryAsk chapters items item (last |> Option.map (fun fact -> fact.Name))
                              Held = held
                              Read = List.length covered })
        // The session's own name first, because it is the one a person sees before they have
        // scrolled anywhere.
        (titleOwed settled title items |> Option.toList) @ chapterJobs

    /// The fact to record once a pass has answered, whatever it answered.
    ///
    /// `stands` is what the name reads after the write — the model's words when the write
    /// took, and what was already there when it lost the race or the answer was unusable.
    /// Recording the loss matters as much as recording the win: it is what tells the next
    /// pass that this subject is somebody else's now.
    let settle (job: Job) (creator: Principal option) (stands: string) : SessionNamed =
        { Subject = job.Subject
          Name = stands
          Read = job.Read
          OnBehalfOf = creator }
