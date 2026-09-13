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
/// Its memory is the event log. Both questions a pass asks are about the past — may this
/// still be written over, and has enough been said since to be worth asking again — so a
/// `SessionNamed` answers both, and a restarted process picks up exactly where it left off
/// instead of either re-asking everything or refusing to ask anything.
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

    /// Fold one event into what the session has settled so far.
    let applyEvent (acc: Map<NamingSubject, SessionNamed>) (event: SessionEvent) : Map<NamingSubject, SessionNamed> =
        match event with
        | SessionNamed named -> Map.add named.Subject named acc
        | _ -> acc

    let ofEvents (events: SessionEvent list) : Map<NamingSubject, SessionNamed> =
        events |> List.fold applyEvent Map.empty

    /// Whether the session may still write this name, given what it last settled it to.
    ///
    /// Two ways to be allowed and one to be finished. The guess is writable because nobody
    /// chose it; the session's own last answer is writable because it wrote it. Anything else
    /// is a person's words, and a person's words end the question for good — there is no
    /// re-asking a subject somebody has named, however much is said afterwards.
    let private ours (settled: SessionNamed option) (held: string) : bool =
        match settled with
        | Some last -> held = last.Name
        | None -> false

    /// Whether enough has been said since the last ask to be worth asking again.
    ///
    /// Doubling rather than any growth at all. A chapter's stretch grows with every message
    /// until the next chapter opens, so "ask when anything is new" is one model call per
    /// message for as long as the session lasts; doubling makes it a handful over a whole
    /// session. And it still catches the case the rule is for — a session that opened with
    /// "run tests" and put the actual work in the second message has doubled by the time that
    /// message lands.
    let private worthAsking (settled: SessionNamed option) (covered: int) : bool =
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
        (settled: Map<NamingSubject, SessionNamed>)
        (title: string)
        (items: ConversationItem list)
        : Job option =
        let last = Map.tryFind NamingSubject.Title settled
        if not (title = "" || ours last title) then None
        elif not (worthAsking last (List.length items)) then None
        else
            Some
                { Subject = NamingSubject.Title
                  Ask = Titles.summaryAsk items (last |> Option.map (fun fact -> fact.Name))
                  Held = title
                  Read = List.length items }

    let owed
        (settled: Map<NamingSubject, SessionNamed>)
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
