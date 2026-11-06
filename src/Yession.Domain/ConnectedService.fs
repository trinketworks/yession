namespace Yession.Domain.Access

/// Which service a person's own connection is for, as the agent is told it. Only the two a
/// person connects from a session's settings; the Manager cannot tell them apart (the header
/// of `Connections.fs` says why), so the session names the service from the storage name it
/// chose.
///
/// Declared ahead of the events, because an offline ask (`OfflineAccessAsked`) and the wake it
/// owes name it.
[<RequireQualifiedAccess>]
type ConnectedService =
    | GitHub
    | Claude

module ConnectedService =

    /// The word the agent's tools take, and the word the wire keeps.
    let word (service: ConnectedService) : string =
        match service with
        | ConnectedService.GitHub -> "github"
        | ConnectedService.Claude -> "claude"

    let ofWord (word: string) : ConnectedService option =
        match word with
        | "github" -> Some ConnectedService.GitHub
        | "claude" -> Some ConnectedService.Claude
        | _ -> None

    /// What a person calls it.
    let name (service: ConnectedService) : string =
        match service with
        | ConnectedService.GitHub -> "GitHub"
        | ConnectedService.Claude -> "Claude"
