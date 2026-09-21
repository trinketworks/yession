namespace Yession.App

/// A command of ours on its way into a read model that arrives SEPARATELY.
///
/// The settings drawer's panels are CQRS with the seam left implicit. A panel posts a
/// command; the Manager answers "accepted" as soon as it has stored the credential; the
/// status the panel RENDERS arrives later, off a stream that walks back through a decrypt
/// per credential before it says anything. The panel used to take the command's 200 for the
/// answer, probe the query once, and render whatever that probe happened to catch — so
/// which answer it got was a race, and losing it was permanent, because nothing asked
/// again. That is the whole fault, and it is not the transport's to fix: eventual
/// consistency is the design, and a reader that assumes otherwise is the bug.
///
/// So the gap is a STATE. `Awaiting` says a command of ours is on its way into the query
/// and names the fact that will end the wait; everything else on the panel keeps rendering
/// the last thing the query actually said.
///
/// Deliberately not optimistic: nothing here fabricates an answer, so nothing has to be
/// rolled back. A fabricated connection row would have to state a `Kind` and a health only
/// the Manager knows, and then be unwound when the real one disagreed. "What is true, plus
/// what is in flight" is the same immediate feedback with none of the lying.
///
/// The expectation is DATA, and the rule that reads it is a PARAMETER of `observed` rather
/// than a field. A closure in the model would make the model incomparable and a red test
/// unable to print what the panel was waiting for; and a rule that travels beside the state
/// instead of inside the fold is a rule every caller has to remember to apply.
[<RequireQualifiedAccess>]
type Pending<'expect> =
    /// Nothing of ours in flight — the query's answer is the whole truth.
    | Ready
    /// Sent; the command has not answered yet.
    | Sending
    /// Accepted, and the query has not shown it yet. `Since` is when it was accepted, so
    /// the wait can END rather than hang (`waited`).
    | Awaiting of expect: 'expect * since: int64
    /// Refused — by the command itself, or by the wait running out.
    | Refused of reason: string

module Pending =

    /// How long a wait may stand before it is reported instead of held. Generous next to
    /// what it covers (a status frame is milliseconds behind the command that moved it) and
    /// short next to anybody's patience for a spinner that will never stop.
    [<Literal>]
    let deadlineMillis = 10_000L

    /// What a wait that outlived its deadline says. The write WAS accepted — "failed" would
    /// be a lie and a retry prompt would be worse, because retrying a stored credential
    /// stores it again. What is true is that this panel never saw it.
    [<Literal>]
    let unseen = "saved — this panel has not seen it yet"

    /// The query spoke. A wait ends when what it said is what the command was waiting for,
    /// and otherwise STANDS: a status that has not caught up yet is not a status that
    /// refused, and treating the two the same is the fault this type exists to stop.
    let observed (landed: 'expect -> 'fact -> bool) (fact: 'fact) (pending: Pending<'expect>) : Pending<'expect> =
        match pending with
        | Pending.Awaiting (expect, _) when landed expect fact -> Pending.Ready
        | pending -> pending

    /// The clock moved.
    let waited (now: int64) (pending: Pending<'expect>) : Pending<'expect> =
        match pending with
        | Pending.Awaiting (_, since) when now - since >= deadlineMillis -> Pending.Refused unseen
        | pending -> pending

    /// Is something of ours in flight? A panel with a command on the way shows it working
    /// rather than offering the controls that would send a second one.
    let inFlight (pending: Pending<'expect>) : bool =
        match pending with
        | Pending.Sending
        | Pending.Awaiting _ -> true
        | Pending.Ready
        | Pending.Refused _ -> false

    /// Why the last command failed, when one did.
    let refusal (pending: Pending<'expect>) : string option =
        match pending with
        | Pending.Refused reason -> Some reason
        | Pending.Ready
        | Pending.Sending
        | Pending.Awaiting _ -> None
