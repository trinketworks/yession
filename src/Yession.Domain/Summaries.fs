namespace Yession.Domain.Agent

// The summarization seam, and only that. Its own file because of WHERE it has to be: the
// rule that builds an ask lives with the state it is about (`Chapters`, in Conversation.fs),
// and the rest of this namespace is compiled after that — so a seam declared beside
// `RunAgent` would be one no domain rule could name.

/// A few words about some conversation, and what they are FOR.
///
/// The task and the budget travel WITH the lines because the surface the words go on is what
/// knows them — a chapter's rule holds one line of 48 characters, and a provider told only
/// "summarize this" would be a provider inventing a length and a register of its own. What is
/// left to an implementation is how it asks a model, which is the only part of this a second
/// implementation would do differently.
type SummaryAsk =
    { /// What the words are for, written to be handed to a model as it stands.
      Task : string
      /// What to read, oldest first, already flattened to plain lines — no conversation types
      /// cross this seam, so something outside this domain could answer it.
      Lines : string list
      /// The most characters the answer may run to. Advisory at the provider; the caller
      /// shapes what comes back regardless, because a model is not a length check.
      Budget : int }

/// Write a few words, if anything here can.
///
/// Held as an `option` at the composition root for the reason `RunAgent` is: a deployment with
/// no credential is not a broken deployment, it is one where nothing summarizes — and every
/// caller of this already has an answer for that, because it is the answer they give today.
///
/// A failure is a VALUE for the same reason a turn's is: nothing here is exceptional. A model
/// that refused, a credential that has gone stale and a provider that never answered are all
/// "no words this time", and the caller's fallback is the same in each case.
type Summarize = SummaryAsk -> Async<Result<string, string>>
