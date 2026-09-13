namespace Yession.SessionProcess

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Chat
open Yession.Domain.Collab

/// Naming the chapters nobody has named (Plan 25).
///
/// A chapter is made with a guess on it — the first line of the message it opens at — and the
/// guess is what a person edits when they want something better. This is the other way it
/// gets something better: while a chapter still wears its guess, whatever can write a few
/// words is asked for some, and what comes back replaces the guess in the session.
///
/// Shaped like the queue drain, because it is the same kind of thing: a doc update might have
/// made work, so look and see. Single-flight for the same reason (its own write is a doc
/// update), and idempotent for a better one — a chapter is asked about ONCE per process, so a
/// model that answers badly costs one call rather than one per keystroke anybody types
/// afterwards.
module ChapterNames =

    /// Create the namer for one session: a thing to call on every doc update, like the drain
    /// — a chapter is made by a doc write and nothing else announces it.
    ///
    /// A bare function rather than a record of one, because there is one thing to do with it.
    ///
    /// `summarize` is a THUNK, read at each pass for the reason `runAgent` is: a credential
    /// connected mid-session starts naming chapters without a relaunch, and `None` at pass
    /// time means the guesses simply stand.
    ///
    /// `readConversation` is injected rather than reached for: the chapters are in the doc and
    /// the items they are about are in the log, and this is the one place the two meet.
    let create
        (doc: Yjs.Y.Doc)
        (readConversation: unit -> Async<ConversationProjection>)
        (summarize: unit -> Summarize option)
        : unit -> unit =
        // Asked about, whatever came back. A failure is not retried: a provider that refused
        // this session's credential will refuse it again, and a namer that kept trying would
        // spend a request per doc update for as long as the session lasts.
        let asked = System.Collections.Generic.HashSet<string> ()
        let mutable running = false

        /// The chapters worth a look: open, and not already asked about. Read off the doc
        /// alone, so the common pass — nothing new — costs one small structural read and
        /// never touches the log.
        ///
        /// Which also decides something worth saying out loud: a chapter an act opens by
        /// NATURE has no entry in the doc until somebody touches it, so it is not here and is
        /// never named. That is the right line rather than an oversight — an act note is a
        /// sentence somebody already wrote short ("PR octo/hello#12 merged"), and what this
        /// names is where a person divided the session and left the guess standing.
        let candidates () =
            SyncedStateSync.chaptersOf doc
            |> Map.toList
            |> List.filter (fun (messageId, mark) -> mark.Opens && not (asked.Contains (MessageId.value messageId)))
            |> List.map fst

        let nameOne (chapters: Map<MessageId, ChapterMark>) (items: ConversationItem list) (item: ConversationItem) =
            async {
                match summarize () with
                | None -> return ()
                | Some summarize ->
                    // Marked before the call, not after: what must not happen twice is the
                    // ASKING, and an answer that never comes back is exactly the case where
                    // an "after" would ask again on the next update, and the one after that.
                    asked.Add (MessageId.value item.MessageId) |> ignore
                    let guess = Chapters.name chapters item
                    match! summarize (Chapters.summaryAsk chapters items item) with
                    | Error _ ->
                        // Nothing to say and nobody to say it to: the guess is already on the
                        // rule, and a session that announced every unwritten name would be
                        // announcing a provider's weather.
                        return ()
                    | Ok said ->
                        match Chapters.shaped said with
                        | None -> return ()
                        | Some name ->
                            // Against the guess this pass read, which is what makes a person
                            // who typed while the model was thinking the one whose name wins.
                            SyncedStateSync.nameChapter doc item.MessageId guess name |> ignore
                            return ()
            }

        let pass () =
            async {
                match candidates () with
                | [] -> return ()
                | _ ->
                    // The log is read only when there is something to name, and the whole of
                    // it, because a chapter can be anywhere in a session.
                    let! conversation = readConversation ()
                    // Re-read the chapters against the items: whether a chapter is still
                    // wearing its guess is a question about both, and the doc may have moved
                    // while the log was being read.
                    let chapters = SyncedStateSync.chaptersOf doc
                    let unnamed =
                        candidates ()
                        |> List.choose (fun messageId ->
                            conversation.Items |> List.tryFind (fun item -> item.MessageId = messageId))
                        |> List.filter (Chapters.unwritten chapters)
                    for item in unnamed do
                        do! nameOne chapters conversation.Items item
            }

        fun () ->
            if not running then
                running <- true
                Async.StartImmediate (
                    async {
                        try
                            do! pass ()
                        finally
                            running <- false
                    })
