namespace Yession.Domain.Standings

/// How much a standing wants a person, least first. Its own ladder rather than a
/// `QueryTone`, because a tone says how a thing WENT and this says who the ball is with:
/// an idle agent ("waiting") and a healthy pull request are both fine, and only the first
/// is somebody's turn.
type Attention =
    /// Nothing to say: the session is stopped, or what it watches is fine.
    | Rest
    /// Nobody is there to do anything, and the person chose that knowingly: no agent.
    | Absent
    /// Under way, and nobody is needed for it to finish: a turn, a command, a check.
    | Moving
    /// A person's turn: the agent is idle and the next word is theirs.
    | Wanted
    /// Something went wrong, and nothing moves until a person fixes it.
    | Wrong

module Attention =

    /// One word, the same on the wire and in every `data-` hook that spells it.
    let name (attention: Attention) : string =
        match attention with
        | Rest -> "rest"
        | Absent -> "absent"
        | Moving -> "moving"
        | Wanted -> "wanted"
        | Wrong -> "wrong"

    let parse (raw: string) : Attention option =
        match raw with
        | "rest" -> Some Rest
        | "absent" -> Some Absent
        | "moving" -> Some Moving
        | "wanted" -> Some Wanted
        | "wrong" -> Some Wrong
        | _ -> None

/// One thing a session says about itself to whoever lists it — the Manager's roster, the
/// session page's header band, the tab title. The listener learns what a STATUS is and never
/// what it is about: `Facet` is a key it dedupes by and never reads, `Word` is the sentence
/// it shows, and `Attention` is how much it wants a person.
///
/// The middle of three shapes. An opaque line was too little: a listener could not colour
/// it, rank it, or pick the one to show when there is room for one. A shared vocabulary of
/// what sessions do was too much: the Manager would have to learn "conflicted" and "waiting"
/// and every word a producer adds after them.
///
/// No time. A listener stamps arrival per facet change itself, because the producer's clock
/// is not one it may trust.
type Standing =
    { Facet : string
      Word : string
      Attention : Attention }

module Standing =

    /// Most first.
    let private concern (attention: Attention) : int =
        match attention with
        | Wrong -> 0
        | Wanted -> 1
        | Moving -> 2
        | Absent -> 3
        | Rest -> 4

    /// The one to show where there is room for one. Ties go to the earlier, so a producer
    /// that lists its facets in a fixed order gets a stable answer.
    let worst (standings: Standing list) : Standing option =
        // A fold rather than `List.minBy`, whose tie under Fable is the LAST.
        standings
        |> List.fold
            (fun held s ->
                match held with
                | Some h when concern h.Attention <= concern s.Attention -> held
                | _ -> Some s)
            None

    /// All of them, for a surface with room for all, worst first and otherwise in the
    /// order given.
    let byConcern (standings: Standing list) : Standing list =
        standings |> List.sortBy (fun s -> concern s.Attention)

/// What the session knows about its agent right now — the same facts the host already reads
/// to decide whether it is in use, put where a cheap test reaches the rule over them.
type AgentSituation =
    { /// A turn is running.
      TurnRunning : bool
      /// The terminals running a command the agent asked for in the background, by the name
      /// people know them by (`Projection.awaitedByAgent`). Each owes the agent a wake, so it
      /// is the agent's work in flight.
      BusyTerminals : string list
      /// No agent can run here: nobody has connected one and the environment holds none.
      /// Not "a message is waiting for one", because without an agent the queue drains
      /// straight into the conversation and nothing ever waits.
      NoAgent : bool
      /// The agent's provider wants somebody to sign in before it can run.
      SignInRequired : bool }

/// What the agent is doing, said as one standing. "Waiting" is the honest word for idle: the
/// ball is with the people, and the log holds no fact that says the agent asked them
/// something, so this does not guess at one.
///
/// The words are the ones the session page's sidebar already says for the same states
/// ("sign in again", "no agent"), because a state has one name on every screen
/// (docs/visual-design.md, Colour). "Waiting" is a person's turn, so it asks for more than
/// work under way does, which asks for nobody. "No agent" is a choice somebody made, not a
/// wrong thing, so it is the quietest of the five.
module AgentStatus =

    let facet = "agent"

    let noAgent = "no agent"
    let signInAgain = "sign in again"

    /// A running turn is work whatever else holds, because it proves an agent is there and
    /// signed in. Otherwise the two that need a person outrank a command still running,
    /// which is under way and needs nobody.
    ///
    /// "running" names no command. A command's name is a sentence of its own, and it took
    /// the roster row's and the header's room; the composer's tray names it, one press from
    /// the terminal.
    let standing (situation: AgentSituation) : Standing =
        let say word attention = { Facet = facet; Word = word; Attention = attention }
        if situation.TurnRunning then say "working" Moving
        elif situation.SignInRequired then say signInAgain Wrong
        elif situation.NoAgent then say noAgent Absent
        elif List.isEmpty situation.BusyTerminals then say "waiting" Wanted
        else say "running" Moving
