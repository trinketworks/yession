namespace Yession.App

open Fable.Core
open Yjs
open Yession.Domain
open Yession.Domain.Collab
open Yession.App.Collab

/// The draft-slot publication rule: a peer's slot is in the synced state IFF that peer's body
/// has content.
///
/// The slot is what every OTHER client renders a draft box from, so publishing one when the
/// composer mounted — what the browser used to do — put an empty box on every peer's composer
/// for everyone who had ever opened the session, and left the slot in the doc forever after they
/// left. Publication follows the body instead: the first keystroke materialises the slot,
/// emptying the body retracts it.
///
/// Nothing else has to change, because the body is NOT inside the slot: it is a top-level
/// fragment root (Collab/Bodies.fs) that exists independently, so the local composer still mounts and
/// types before any slot exists — and a send, which removes the slot, still carries the body over.
module DraftSlot =

    /// Whether a body carries anything a send would snapshot.
    type BodyContent =
        | Empty
        | HasContent

    /// Whether the doc announces the peer's draft to its collaborators.
    type SlotState =
        | Published
        | Unpublished

    /// What a body and its slot disagree about, if anything.
    type Reconciliation =
        | Publish
        | Retract
        | Agreed

    /// What to do about a body and its slot. Distinct types for the two facts, so a call site
    /// cannot transpose them, and no wildcard, so the compiler is the one asserting every state is
    /// answered. Pure: publishing needs a fresh queue key, and minting is `settle`'s job.
    let reconcile (body: BodyContent) (slot: SlotState) : Reconciliation =
        match body, slot with
        | HasContent, Unpublished -> Publish
        | Empty, Published -> Retract
        | HasContent, Published -> Agreed
        | Empty, Unpublished -> Agreed

    /// A peer's body as the rule sees it. Emptiness is measured in Markdown — the same read a
    /// send snapshots and the drain durably records — so "publishes a slot" and "sends something"
    /// can never disagree. (Fragment length would be cheaper and wrong: mounting an editor writes
    /// an empty paragraph, and an empty paragraph is not a draft.)
    ///
    /// Files attached to the draft are content too: a person who attaches a file before typing a
    /// word has started a message, and retracting its slot would orphan the file.
    let contentOf (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) : BodyContent =
        if (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer))).Trim () = ""
           && not (SyncedStateSync.draftAttached doc peer) then Empty
        else HasContent

    /// Whether the doc currently announces this peer's draft.
    let slotOf (doc: Y.Doc) (peer: PeerId) : SlotState =
        if SyncedStateSync.hasDraft doc peer then Published else Unpublished

    /// The queue key a newly published draft carries: minted once, by its author, and read by
    /// every co-editor that later sends it.
    let private mintQueueId () : QueueId =
        match QueueId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "queue id invariant violated: %s" e

    /// Bring the local peer's slot into agreement with its body, now. Both facts are read from
    /// the doc, so the rule never acts on a stale model snapshot.
    let settle (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) (dispatch: Sink<ClientMsg>) : unit =
        match reconcile (contentOf doc registry peer) (slotOf doc peer) with
        | Publish -> dispatch (EnsureDraftMsg (peer, mintQueueId ()))
        | Retract -> dispatch (DiscardDraftMsg peer)
        | Agreed -> ()

    // The body is a `Y.XmlFragment`, so its own change events are the exact signal this rule
    // needs — `observeDeep` fires for a keystroke and for a merged remote edit, and for nothing
    // else. Watching the whole doc instead would re-run the rule (and a Markdown serialize) on
    // every title edit, queue edit, and peer's draft.

    /// Keep the local peer's slot in step with its own body: settle now, then on every change to
    /// that body. Only the local peer's slot — publication is the author's, exactly as sending is.
    /// The returned `Subscription` stops observing.
    ///
    /// The body and the attachments are the only things that can move the slot under this rule,
    /// so nothing else is watched. State that arrives another way — a persisted doc replaying at
    /// load, say — is answered by calling `settle` at that point, which is what the browser does
    /// once its IndexedDB store has synced.
    let follow (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) (dispatch: Sink<ClientMsg>) : Subscription =
        let fragment = registry.Fragment (BodyKey.draft peer)
        let attachments : Y.Map<obj> = doc.getMap "attachments"
        // One observer value for each pair of calls: `unobserve` removes a listener only when
        // handed the very reference `observe` was given. The events and the transaction they
        // carry are not read — the rule re-reads the doc.
        let observer _ _ = settle doc registry peer dispatch
        let attachmentsObserver _ _ = settle doc registry peer dispatch
        settle doc registry peer dispatch
        fragment.observeDeep observer
        attachments.observe attachmentsObserver
        Subscription.ofStop (fun () ->
            fragment.unobserveDeep observer
            attachments.unobserve attachmentsObserver)

/// The same publication rule for a terminal composer (Plan 13). Identical in shape and in
/// reasoning — a slot exists exactly while its command line has content — over a plain
/// `Y.Text` instead of a rich body, so emptiness is a string test rather than a Markdown
/// serialize.
module TerminalDraftSlot =

    let private mintQueueId () : QueueId =
        match QueueId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "queue id invariant violated: %s" e

    /// Bring the local peer's slot in this terminal into agreement with its command line.
    /// Both facts are read from the doc, never from a model snapshot, so the rule cannot
    /// act on a stale pair.
    let settle
        (doc: Y.Doc)
        (registry: TextRegistry)
        (terminal: TerminalId)
        (peer: PeerId)
        (dispatch: Sink<ClientMsg>)
        : unit =
        let content =
            if (registry.Text (BodyKey.terminalDraft terminal peer)).toString().Trim () = ""
            then DraftSlot.Empty
            else DraftSlot.HasContent
        let slot =
            if SyncedStateSync.hasTerminalDraft doc terminal peer then DraftSlot.Published else DraftSlot.Unpublished
        match DraftSlot.reconcile content slot with
        | DraftSlot.Publish -> dispatch (EnsureTerminalDraftMsg (terminal, peer, mintQueueId ()))
        | DraftSlot.Retract -> dispatch (DiscardTerminalDraftMsg (terminal, peer))
        | DraftSlot.Agreed -> ()

    /// Keep the local peer's slot in step with its own command line, in one terminal.
    let follow
        (doc: Y.Doc)
        (registry: TextRegistry)
        (terminal: TerminalId)
        (peer: PeerId)
        (dispatch: Sink<ClientMsg>)
        : Subscription =
        let text = registry.Text (BodyKey.terminalDraft terminal peer)
        // One observer value for both calls, as above.
        let observer _ _ = settle doc registry terminal peer dispatch
        settle doc registry terminal peer dispatch
        text.observe observer
        Subscription.ofStop (fun () -> text.unobserve observer)
