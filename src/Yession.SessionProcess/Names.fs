namespace Yession.SessionProcess

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Chat
open Yession.Domain.Collab

/// Naming what nobody has named (Plan 25).
///
/// The rule for WHAT is owed a name is `Naming.owed`, in the domain, where a cheap test can
/// reach it. This is the half that cannot be pure: reading the doc and the log, asking
/// whatever can write a few words, putting the answer back, and recording that it asked.
///
/// Shaped like the queue drain, because it is the same kind of thing: something might have
/// made work, so look and see. Single-flight for the same reason — its own write is a doc
/// update, and its own record is a log append, so both of its triggers fire on the way out.
module Names =

    /// Create the namer for one session: a thing to call whenever something might have made
    /// naming work. Three things can, and only three: a chapter is made (a doc write), more
    /// is said (a log append), and a credential arrives (neither, which is why the Manager's
    /// connection stream reaches this too).
    ///
    /// `summarize` is read at each pass rather than held, for the reason `runAgent` is: a
    /// credential connected mid-session starts naming without a relaunch, and `None` at pass
    /// time means the guesses simply stand.
    ///
    /// `creator` likewise — the log answers it, and at boot it answers `None` because nobody
    /// has joined yet.
    let create
        (doc: Yjs.Y.Doc)
        (readEvents: unit -> Async<EventEnvelope<SessionEvent> list>)
        (latestOffset: unit -> EventOffset option)
        (record: SessionNamed -> Async<unit>)
        (creator: unit -> Principal option)
        (summarize: unit -> Summarize option)
        : unit -> unit =
        let mutable running = false
        /// What the last pass was answered against: the doc's title and chapters, and how far
        /// the log had got. Between them they are everything `Naming.owed` reads, so a trigger that
        /// leaves both alone — somebody typing in a draft, a terminal record landing — cannot
        /// have made naming work, and does not read the log to discover that. `None` until
        /// the first pass, because a session that has looked at nothing has to look once.
        let mutable lastSeen : (string * Map<MessageId, ChapterMark> * EventOffset option) option = None

        /// Everything a pass would answer against, cheaply: one small structural read of the
        /// chapters root and the offset the log is already keeping.
        let here () = SyncedStateSync.titleOf doc, SyncedStateSync.chaptersOf doc, latestOffset ()

        /// What stands on a chapter after trying to write `name` over `held`.
        ///
        /// The write is a compare-and-set, and losing it is not a failure: somebody typed
        /// while the model was thinking, and their name is the one the session keeps. What
        /// matters is that the RECORD says so, because that is what stops the next pass
        /// offering to write over them.
        let writeChapter (messageId: MessageId) (held: string) (name: string) : string =
            match SyncedStateSync.nameChapter doc messageId held name with
            | "" -> held
            | stands -> stands

        let nameOne (write: Summarize) (job: Naming.Job) =
            async {
                let! answer = write job.Ask
                let stands =
                    match answer |> Result.toOption |> Option.bind Chapters.shaped with
                    | None ->
                        // A provider that refused, or words a name cannot hold. Nothing to
                        // say and nobody to say it to: the guess is already on the rule, and
                        // a session that announced every unwritten name would be announcing
                        // a provider's weather.
                        job.Held
                    | Some name ->
                        match job.Subject with
                        | NamingSubject.Chapter messageId -> writeChapter messageId job.Held name
                        | NamingSubject.Title ->
                            match SyncedStateSync.nameTitle doc job.Held name with
                            | "" -> job.Held
                            | stands -> stands
                // Recorded whatever happened, including nothing happening. A pass that
                // considered a subject and kept the name it had is exactly the fact that
                // stops the next pass asking the same question of the same material — which
                // is what the asked-once set used to do, badly, and only until a restart.
                do! record (Naming.settle job (creator ()) stands)
            }

        let pass () =
            async {
                match summarize () with
                | None -> return ()
                | Some write ->
                    let seen = here ()
                    if lastSeen = Some seen then return ()
                    else
                        let title, chapters, _ = seen
                        lastSeen <- Some seen
                        let! envelopes = readEvents ()
                        let conversation =
                            ConversationProjection.applyEvents None envelopes ConversationProjection.empty
                            |> fst
                        let settled = Naming.ofEvents (envelopes |> List.map (fun e -> e.Event))
                        for job in Naming.owed settled title chapters conversation.Items do
                            do! nameOne write job
            }

        let rec run () =
            if not running then
                running <- true
                // What was true when this pass started, so the re-arm below asks whether
                // something moved DURING it rather than whether there is anything to do.
                // Those are different questions, and answering the second here spins: a
                // session with nothing to write the words never reaches the watermark, so
                // "is the watermark current" is permanently no, and a pass that re-armed on
                // it would re-arm forever and starve everything else in the process.
                let before = here ()
                Async.StartImmediate (
                    async {
                        try
                            do! pass ()
                        finally
                            running <- false
                            // Something moved while this pass was reading — including its own
                            // write and its own record. Whatever triggered on that was
                            // swallowed by the single-flight guard, so the pass that missed
                            // it has to be the one to look again; the look is cheap, and it
                            // settles as soon as nothing is moving.
                            if here () <> before then run ()
                    })

        run
