namespace Yession.Domain

/// What a sentence in this session can point AT, and the sentence that points.
///
/// An act on the timeline names things: the person whose credential a push went out on,
/// the repository it went to, the connection a sandbox forwards. Those used to be
/// `sprintf`'d into one string at the fold, and a string is the one shape in which a screen
/// cannot tell a name from the words around it — so the chat printed `user:ada` where every
/// other surface shows Ada's name and mark, and the "github" a sandbox forwards was a bare
/// word with nothing to say it was the same GitHub the sidebar's connection panel is about.
///
/// So a sentence is SEGMENTS: text, and references to entities. Two readers collapse it,
/// each with its own opinion — the agent's prompt into prose (`Phrase.said`), the screen
/// into text with each reference drawn as that entity is drawn everywhere else on it. The
/// fold builds neither; it says what happened and to whom, and the readers render.

/// A thing a sentence can point at. Closed and small on purpose: a case is added when a
/// family of acts needs to name that kind of thing, and every case has one prose spelling
/// (`EntityRef.said`) and one look on a screen.
[<RequireQualifiedAccess>]
type EntityRef =
    /// A party: a person, the agent, the process, the deployment, a repo's own file.
    | Actor of ActorRef
    /// A GitHub repository.
    | Repo of RepoRef
    /// An external-service connection somebody signed in to — the credential a sandbox
    /// forwards, named and never valued.
    | Connection of ConnectionName
    /// A work sandbox, scope included: the session's own `dev`, or a repo's `octo/hello:dev`.
    | Sandbox of SandboxRef

module EntityRef =

    /// The ONE prose spelling of each kind of thing, for every reader that is not a screen:
    /// the agent's prompt, a digest, a log line, a test.
    ///
    /// An actor is its token (`user:ada`, `agent`), which is what the tools take and what
    /// the actor column already reads. A repo is `github:owner/repo` — the host said in
    /// prose because a screen says it with a mark, and `RepoRef.create` accepts the prefix
    /// so an agent quoting this spelling into `add_repo` is not refused by it. A
    /// connection is its name. A sandbox is `SandboxRef.render` — scope and all, because
    /// prose has no author line over it to say whose `dev` this is.
    let said (entity: EntityRef) : string =
        match entity with
        | EntityRef.Actor actor -> ActorRef.token actor
        | EntityRef.Repo repo -> "github:" + RepoRef.value repo
        | EntityRef.Connection name -> ConnectionName.value name
        | EntityRef.Sandbox sandbox -> SandboxRef.render sandbox

/// One piece of a sentence: words, or a thing the words are about.
[<RequireQualifiedAccess>]
type Segment =
    | Text of string
    | Ref of EntityRef

/// A sentence, as the fold hands it to its readers.
type Phrase = Segment list

module Phrase =

    /// A sentence that names nothing — the ordinary case, and how every act reads until its
    /// family is given references.
    let text (words: string) : Phrase = [ Segment.Text words ]

    /// The sentence as prose. Concatenation and nothing else: the fold has already put the
    /// spaces where they go, so this reader adds none and a screen rendering the same
    /// segments adds none either.
    let said (phrase: Phrase) : string =
        phrase
        |> List.map (fun segment ->
            match segment with
            | Segment.Text words -> words
            | Segment.Ref entity -> EntityRef.said entity)
        |> String.concat ""

    /// Everything this sentence points at, in the order it points.
    let refs (phrase: Phrase) : EntityRef list =
        phrase
        |> List.choose (fun segment ->
            match segment with
            | Segment.Ref entity -> Some entity
            | Segment.Text _ -> None)
