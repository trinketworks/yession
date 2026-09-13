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
///
/// The answer is TYPED rather than written. A name that appeared whole, in a field somebody
/// might be in, is the session editing over people; a name that arrives a few characters at a
/// time behind a caret they can see is a collaborator. The mechanics of the caret and of
/// whose caret is where are the composition's (`Typing`), because relative positions and the
/// presence relay both live above this; the rule about when to type, when not to start, and
/// when to stop is here.
module Names =

    /// How the session types a name where people can watch it.
    ///
    /// Handed in rather than reached for: a caret is a relative position over a shared type
    /// and a presence frame to every peer, and neither is something this module can see from
    /// where it sits. What it decides is the rule — start only where nobody is, stop the
    /// moment somebody arrives — and that needs only these three questions answered.
    type Typing =
        { /// Whether somebody else's caret is in this field right now. The courtesy half of
          /// the rule: a session that began typing into a field a person was already in would
          /// be fighting them for it, however politely it merged.
          Occupied : NamingSubject -> bool
          /// Add to the end of the name, only while it still reads `expected`; answers what
          /// stands. The other half of the rule, and the one that cannot be raced: a tick
          /// that finds other words there wrote nothing, and says so by answering them.
          Append : NamingSubject -> string -> string -> string
          /// Say where the session's caret is, or that it is nowhere.
          Caret : (NamingSubject * int) option -> unit }

    /// How much arrives per tick, and how often. Fast enough not to be a performance, slow
    /// enough to read as somebody typing rather than as a field changing under you.
    let [<Literal>] private PerTick = 3
    let private tick = System.TimeSpan.FromMilliseconds 45.0

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
        (clock: Clock)
        (doc: Yjs.Y.Doc)
        (typing: Typing)
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
        let here () =
            SyncedStateSync.nameOf doc NamingSubject.Title, SyncedStateSync.chaptersOf doc, latestOffset ()

        /// Type `name` into `subject`, a few characters at a time, and answer what stands.
        ///
        /// Starts by clearing what was there, because that is what replacing a name IS: a
        /// person selects the words and types over them, and the guess is not something to
        /// splice against. Every tick after that is an append under the same
        /// compare-and-set, so the first tick that finds words the session did not leave
        /// there stops — somebody typed while it was typing, and theirs is the name the
        /// session keeps.
        ///
        /// The caret is reported after each tick and cleared at the end, whichever way it
        /// ended. A caret left behind by a writer that has stopped is worse than no caret:
        /// it says somebody is still there.
        let typeName (subject: NamingSubject) (held: string) (name: string) =
            async {
                // Clearing what was there IS replacing a name: a person selects the words and
                // types over them. Under the same compare-and-set as every tick after it, so
                // even this first step loses to anybody who got a word in between the model
                // answering and the typing starting.
                let cleared = SyncedStateSync.nameSubject doc subject held ""
                if cleared <> "" then return cleared
                else
                    let mutable stands = ""
                    let mutable stopped = false
                    let mutable at = 0
                    while not stopped && at < name.Length do
                        do! clock.After tick
                        let next = min name.Length (at + PerTick)
                        let addition = name.Substring (at, next - at)
                        let after = typing.Append subject stands addition
                        if after <> stands + addition then
                            // Somebody typed while the session was typing. Theirs is the name
                            // the session keeps, and what it had put down stays where it is —
                            // which is what a person who watched it happen would expect, and
                            // what deleting a range somebody has since typed inside cannot
                            // promise.
                            stands <- after
                            stopped <- true
                        else
                            stands <- after
                            at <- next
                            typing.Caret (Some (subject, stands.Length))
                    // Cleared however it ended: a caret left behind by a writer that has
                    // stopped is worse than no caret, because it says somebody is still there.
                    typing.Caret None
                    return stands
            }

        let nameOne (write: Summarize) (job: Naming.Job) =
            async {
                let! answer = write job.Ask
                let! stands =
                    match answer |> Result.toOption |> Option.bind Chapters.shaped with
                    | None ->
                        // A provider that refused, or words a name cannot hold. Nothing to
                        // say and nobody to say it to: the guess is already on the rule, and
                        // a session that announced every unwritten name would be announcing
                        // a provider's weather.
                        async { return job.Held }
                    | Some name when name = job.Held ->
                        // The answer KEPT the name, which is the commonest right one on a
                        // re-reading. Keeping it means leaving it alone: typing it again
                        // would clear the field and write the same words back, and for the
                        // moment in between the surface falls back to the guess — a name
                        // blinking out and returning unchanged, for a decision that was to
                        // change nothing.
                        async { return job.Held }
                    | Some name when typing.Occupied job.Subject ->
                        // Somebody is in this field with their own caret. Whatever they are
                        // doing there, it is about these words, and they were here first.
                        async { return job.Held }
                    | Some name -> typeName job.Subject job.Held name
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
