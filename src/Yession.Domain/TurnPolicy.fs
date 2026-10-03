namespace Yession.Domain.Chat

open System
open Yession.Domain

/// Who a message is addressed to: `@agent`, or `@name` for somebody who joined this session
/// under that name. Read off the words, against the room as the log knows it — so typing
/// `@agent` addresses the agent with no picker, and `@swift-heron` addresses whoever joined
/// as swift-heron, resolved to their durable user by the same rule that stamps an author.
///
/// Deliberately the whole mechanism: a token that names nobody here is just text, and
/// anything an address cannot settle is the agent's to settle by reading (its prompt says
/// how). The policy below decides from this and nothing else.
type Addressed =
    { /// `@agent` appears.
      Agent : bool
      /// Everyone else named, as who they are.
      People : Principal list }

module Addressed =

    let nobody : Addressed = { Agent = false; People = [] }

    /// The agent's name, as people address it. One spelling, shared with the prompt that
    /// tells the agent what it is called.
    [<Literal>]
    let agentName = "agent"

    let private isNameChar (c: char) = Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.'

    /// Every `@name` token: an `@` at the start or after something that cannot be part of a
    /// name (so `ada@example.com` is an address of a different kind), then name characters,
    /// with trailing `.`/`-` dropped as sentence punctuation.
    let tokens (body: string) : string list =
        let rec scan (i: int) (acc: string list) =
            if i >= body.Length then List.rev acc
            elif body.[i] = '@' && (i = 0 || not (isNameChar body.[i - 1])) then
                let mutable j = i + 1
                while j < body.Length && isNameChar body.[j] do j <- j + 1
                let name = body.Substring(i + 1, j - i - 1).TrimEnd ('.', '-')
                scan j (if name = "" then acc else name :: acc)
            else scan (i + 1) acc
        scan 0 []

    /// Who `body` addresses, among the people `people` names. Case-insensitive, because a
    /// person typing a name is not spelling an identifier.
    let ofBody (people: Attribution.State) (body: string) : Addressed =
        let same (a: string) (b: string) = String.Equals (a.Trim (), b, StringComparison.OrdinalIgnoreCase)
        tokens body
        |> List.fold
            (fun (acc: Addressed) token ->
                if same agentName token then { acc with Agent = true }
                else
                    let named =
                        people.Names
                        |> Map.toList
                        |> List.filter (fun (_, name) -> same name token)
                        |> List.map (fun (peer, _) -> Attribution.principalFor people.PeerUsers peer)
                    { acc with People = acc.People @ named |> List.distinct })
            nobody

/// Whether a batch of messages starts a turn, and which message it answers (Plan: multiplayer
/// conversations). The SCHEDULER is the mechanism — it appends every message, asks this, and
/// runs a turn on the answer's author's credential or runs none — and this is the policy, a
/// value it is handed so the rule can change without touching how turns run.
///
/// A held message is not lost: it is on the record, everybody sees it, and the next turn reads
/// it in its history.
type TurnPolicy = Attribution.State -> MessageSent list -> MessageSent option

module TurnPolicy =

    /// A message is for the agent unless it is addressed only to other people. The newest
    /// such message in the batch is the one the turn answers; a batch with none starts no turn.
    ///
    /// Everything this does not hold — a message to nobody in particular, several people
    /// talking — reaches the agent, whose prompt says to act on an imperative, ask when it
    /// cannot tell, and say nothing to a conversation that was not for it. The heuristics
    /// live there, in words, rather than here in code.
    let addressed : TurnPolicy =
        fun people batch ->
            batch
            |> List.filter (fun message ->
                let to' = Addressed.ofBody people message.Body
                to'.Agent || List.isEmpty to'.People)
            |> List.tryLast
