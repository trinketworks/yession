namespace Yession.Domain.Agent

open Yession.Domain

open System

/// Which model a turn runs on, as a vocabulary that names no provider.
///
/// A model has an id the provider knows it by and a name a person reads, and that is the
/// whole of it. Everything about WHICH provider — the endpoint, the credential, the shape
/// of the reply, the fact that this deployment talks to Anthropic at all — stays on the
/// far side of `ListModels`, so the picker, the synced register and the turn all speak the
/// same provider-neutral pair.

/// A model id, exactly as its provider names it.
///
/// Validated rather than taken on trust: this string is handed to a spawned process as a
/// command-line option, and it arrives from a collaborative register any peer may write.
/// A value with whitespace or control characters in it is not an id any provider has —
/// it is a paste accident or worse, and the moment to refuse it is before it leaves here.
type ModelId = private ModelId of string

module ModelId =

    /// No provider names a model with more than this, and a register any peer may write
    /// should not be able to carry a document.
    let private maxLength = 200

    let create (raw: string) : Result<ModelId, string> =
        if isNull (box raw) then Error "ModelId cannot be null"
        elif String.IsNullOrWhiteSpace raw then Error "ModelId cannot be empty or whitespace"
        else
            let trimmed = raw.Trim ()
            if trimmed.Length > maxLength then
                Error (sprintf "ModelId cannot be longer than %d characters" maxLength)
            elif trimmed |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
                Error "ModelId cannot contain whitespace or control characters"
            else Ok (ModelId trimmed)

    let value (ModelId id) = id

/// One model a provider offers. `Name` is what a person picks from; it falls back to the
/// id where a provider offers no better label, because a picker that renders an empty row
/// is worse than one that renders a raw id.
type AgentModel =
    { Id   : ModelId
      Name : string }

module AgentModel =

    /// A model with a label, defaulting to its id when the provider gave none.
    let create (id: ModelId) (name: string) : AgentModel =
        { Id = id
          Name = if String.IsNullOrWhiteSpace name then ModelId.value id else name.Trim () }

/// Ask the provider which models exist, on a party's authority.
///
/// It takes a credential for the same reason a turn does (Plan 08): the agent has no scope of
/// its own, so every call on a provider runs on somebody's. The catalogue a person can see
/// is the catalogue their credential can see; the deployment asking for itself sees the
/// session's own and the local one.
type ListModels = CredentialFor -> Async<Result<AgentModel list, string>>

/// A kept catalogue and the way to drop it, handed back together because they are two
/// halves of one thing: whoever holds the credential state is the only party that can
/// know a kept answer has stopped being one.
type ModelCatalogueCache =
    { /// The lookup, answering from the kept catalogue while it is still an answer.
      List : ListModels
      /// Drop whatever is kept. Called by whatever holds the credential state, when that
      /// state moves under the key an answer was kept for.
      Forget : unit -> unit }

module ModelCatalogue =

    /// How long a kept catalogue is still an answer.
    ///
    /// Long enough that opening the settings drawer four times is one round trip; short
    /// enough that a model released this morning is offered by lunchtime.
    let freshness = TimeSpan.FromMinutes 10.0

    /// A catalogue kept only while it is still an answer to the question that produced it,
    /// which has three parts and each moves on its own:
    ///
    ///   * WHO is asking. `keyOf` names the credential an actor's calls would run on —
    ///     local and cheap, which is what lets a hit cost nothing at all — and the answer
    ///     is kept under it. This used to be kept under nothing, so the first person to
    ///     open the picker filled it for everybody and the next was served a catalogue
    ///     their own credential had never been asked for. A catalogue is a fact about a
    ///     credential; keeping it as a fact about the session is what made that possible.
    ///   * WHICH credential that is. A sign-in, a disconnect or a swap can move it without
    ///     moving the key, so `Forget` is called by whatever holds that state.
    ///   * WHAT the provider offers. Nothing tells a session that a model was released, and
    ///     sessions here live for days, so `ttl` bounds how long an answer stands. The
    ///     comment this replaced said a provider's catalogue does not move while a session
    ///     runs; it does, and a session that had been up a week could not be told.
    ///
    /// One answer is kept, not one per key: a second asker on a different credential
    /// replaces it, which costs a round trip and never serves a list that was somebody
    /// else's.
    ///
    /// Only a SUCCESS is kept. A failure here is almost always "nothing is connected yet",
    /// and caching that would leave the picker permanently empty for a session that signs
    /// in a minute later — which is precisely the state the connection panel exists to get
    /// out of. So a failure is reported and forgotten, and the next asker tries again.
    let keyed
        (now: unit -> DateTimeOffset)
        (ttl: TimeSpan)
        (keyOf: CredentialFor -> 'key)
        (lookup: ListModels)
        : ModelCatalogueCache =
        let mutable answer : ('key * AgentModel list * DateTimeOffset) option = None
        { Forget = fun () -> answer <- None
          List =
            fun actor ->
                async {
                    let key = keyOf actor
                    let asked = now ()
                    match answer with
                    | Some (kept, models, at) when kept = key && asked - at < ttl -> return Ok models
                    | _ ->
                        match! lookup actor with
                        | Error reason -> return Error reason
                        | Ok models ->
                            answer <- Some (key, models, asked)
                            return Ok models
                } }

    /// The models a catalogue offers, ordered for a person: by name, case-insensitively.
    /// A provider's own order is its release order, its internal ordering, or nothing at
    /// all — none of which is what somebody scanning a list is looking for.
    let ordered (models: AgentModel list) : AgentModel list =
        models |> List.sortBy (fun m -> m.Name.ToLowerInvariant (), ModelId.value m.Id)

/// How the agent's prompt is put together: which rules run for a turn, and where each one's
/// text goes (`Prompting` in Yession.Session holds every strategy's rules). Closed on purpose:
/// a strategy is code, chosen by name, never supplied from outside.
[<RequireQualifiedAccess>]
type PromptStrategy =
    /// Every product section in the system prompt, the operator's guidance after them, and
    /// the turn's context as one message.
    | Static
    /// Claude Code's architecture: the product sections in a prefix every turn on every host
    /// shares, what varies by host and turn after the cache boundary, and the turn's context
    /// as reminders beside what the turn answers.
    | ClaudeCodeLike

module PromptStrategy =

    /// The spelling an operator writes and a reader is shown. One table, so a strategy cannot
    /// be read under one name and reported under another.
    let name (strategy: PromptStrategy) : string =
        match strategy with
        | PromptStrategy.Static -> "static"
        | PromptStrategy.ClaudeCodeLike -> "claude-code-like"

    /// Every strategy. A new case breaks `name` above, which is the reminder to list it here.
    let all : PromptStrategy list = [ PromptStrategy.Static; PromptStrategy.ClaudeCodeLike ]

/// One host the agent's CLI may reach, in the form srt's `allowedDomains` matches: a name
/// (`docs.rs`), or `*.` and a name for every subdomain of it at any depth (`*.python.org`),
/// either with an optional `:port`. Checked when the profile is read, so an entry srt would
/// refuse stops the boot and not the first turn, and an entry srt would read differently
/// (`**.`, a CIDR range, a scheme or a path) is refused rather than guessed at.
type AgentHost = private AgentHost of string

module AgentHost =

    let private label (text: string) =
        text <> ""
        && text.Length <= 63
        && not (text.StartsWith "-")
        && not (text.EndsWith "-")
        && text |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')

    let private name (text: string) = text.Split '.' |> Array.forall label

    let private port (text: string) =
        match System.Int32.TryParse text with
        | true, n -> n >= 1 && n <= 65535 && string n = text
        | _ -> false

    /// A host from what an operator wrote, lower-cased, or the sentence saying why it is not one.
    let create (raw: string) : Result<AgentHost, string> =
        let entry = raw.Trim().ToLowerInvariant ()
        let host, portOk =
            match entry.LastIndexOf ':' with
            | -1 -> entry, true
            | at -> entry.Substring (0, at), port (entry.Substring (at + 1))
        let wild = host.StartsWith "*."
        let named = if wild then host.Substring 2 else host
        if entry = "" then Error "a network entry is empty. Write a host, like docs.rs"
        elif not portOk then Error (sprintf "'%s' has a port that is not a number from 1 to 65535" raw)
        elif not (name named) then
            Error (
                sprintf
                    "'%s' is not a host. Write a name (docs.rs) or a name under '*.' (*.python.org), with an optional :port. A scheme, a path, '**.' and an address range are not hosts"
                    raw)
        elif wild && not (named.Contains ".") then
            Error (sprintf "'%s' allows every host under a top-level domain. Name the domain under it, like *.example.com" raw)
        else Ok (AgentHost entry)

    let value (AgentHost host) : string = host

/// What this host's operator says about the agent (`agent:` in the profile): words for it, the
/// strategy that puts its prompt together, where its CLI may reach, and the SDK built-ins it
/// may use. Read once at boot, because a host does not change its mind between turns.
type AgentProfile =
    { /// Appended after the product's system prompt, introduced as the operator's. `None`
      /// appends nothing.
      Guidance : string option
      Prompt : PromptStrategy
      /// Every host the agent's CLI may reach under srt (`agent.network.allow`): the API it
      /// talks to, and whatever a fetch may read. ONE list, because the CLI is one process
      /// and srt confines it whole — the hosts the CLI needs and the hosts `WebFetch` may
      /// read cannot be told apart below it. A profile that names a list replaces the
      /// default whole, so the boot reports one list and nothing is merged.
      Network : AgentHost list
      /// The SDK's own `WebSearch`. The provider runs the search, so it reaches no host the
      /// CLI does not already reach; it is still the operator's opt-in, because not every
      /// provider offers it and what a turn may look up is this host's policy.
      WebSearch : bool
      /// The SDK's own `WebFetch`. The CLI fetches the page itself, so `Network` is the
      /// whole of what it may read, and only srt enforces that (`admits`).
      WebFetch : bool }

module AgentProfile =

    /// The hosts the agent needs and nothing else: the API, and the console and site it
    /// refreshes an OAuth credential against.
    let defaultNetwork : AgentHost list =
        [ AgentHost "api.anthropic.com"; AgentHost "console.anthropic.com"; AgentHost "claude.ai" ]

    /// A host that says nothing about the agent: no guidance, the static prompt, the default
    /// network, and no built-ins.
    let defaults : AgentProfile =
        { Guidance = None
          Prompt = PromptStrategy.Static
          Network = defaultNetwork
          WebSearch = false
          WebFetch = false }

    /// The SDK built-ins a turn on this host may use, by the SDK's own names. Every other
    /// built-in stays out of the model's context (`Agent.optionsFor`).
    let builtinTools (profile: AgentProfile) : string list =
        [ if profile.WebSearch then "WebSearch"
          if profile.WebFetch then "WebFetch" ]

    /// Whether the agent's CLI may run as this profile says under `backend`. A fetch reaches
    /// what the CLI's process can reach, and only srt narrows that to `Network`: under
    /// `host` the same fetch reaches this machine's loopback, the Manager's port and every
    /// network the host is on. So `WebFetch` without srt is refused at boot, not run with a
    /// list nothing enforces.
    let admits (backend: Yession.Domain.Sandboxes.SandboxBackend) (profile: AgentProfile) : Result<unit, string> =
        match backend with
        | Yession.Domain.Sandboxes.SrtBackend -> Ok ()
        | _ when not profile.WebFetch -> Ok ()
        | _ ->
            Error
                "agent web-fetch needs the agent CLI under srt, which is what limits a fetch to agent.network.allow. Set YESSION_SESSION_AGENT_BACKEND=srt, or remove web-fetch"
