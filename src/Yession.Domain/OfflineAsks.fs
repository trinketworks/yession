namespace Yession.Domain.Away

open Yession.Domain
open Yession.Domain.Access

/// The offline asks still open: asked (`OfflineAccessAsked`) and not yet settled
/// (`OfflineAccessSettled`).
///
/// Folded from the log by BOTH sides, as `Waits` is: the session to refuse a second ask and to
/// know what to settle, a client to know which card to show. One reading of one log, so the
/// card a person sees and the ask the session holds open cannot disagree.
///
/// One ask per person and connection at a time. A settled ask is gone; only a new ask from
/// the agent opens it again, which is what keeps a dismissed card from coming back.
module OfflineAsks =

    /// A fold state, so a client can fold the log in pages. Oldest first.
    type Open = private Open of OfflineAccessAsked list

    let empty : Open = Open []

    /// The grant a held row sets: the session settings' own choice, never `Off`. A function
    /// of this module rather than a module beside the `OfflineAllowance` type, which is
    /// declared in another file of the same namespace.
    let grantOf (allowance: OfflineAllowance) : OfflineChoice =
        match allowance with
        | OfflineAllowance.ThisSession -> OfflineChoice.ThisSession
        | OfflineAllowance.AllMySessions -> OfflineChoice.AllMySessions

    /// What a grant read from this session allows, or `None` when it allows nothing.
    let allowanceOf (choice: OfflineChoice) : OfflineAllowance option =
        match choice with
        | OfflineChoice.ThisSession -> Some OfflineAllowance.ThisSession
        | OfflineChoice.AllMySessions -> Some OfflineAllowance.AllMySessions
        | OfflineChoice.Off -> None

    let private same (person: UserId) (connection: ConnectedService) (a: OfflineAccessAsked) =
        a.Person = person && a.Connection = connection

    let apply (Open asks) (events: SessionEvent list) : Open =
        events
        |> List.fold
            (fun asks event ->
                match event with
                | SessionEvent.OfflineAccessAsked a ->
                    match asks |> List.exists (same a.Person a.Connection) with
                    | true -> asks
                    | false -> asks @ [ a ]
                | SessionEvent.OfflineAccessSettled s -> asks |> List.filter (same s.Person s.Connection >> not)
                | _ -> asks)
            asks
        |> Open

    /// Every open ask, oldest first.
    let asks (Open asks) : OfflineAccessAsked list = asks

    /// The whole log at once: the session's reading.
    let pending (events: SessionEvent list) : OfflineAccessAsked list = apply empty events |> asks

    /// The open ask for this person's connection, if there is one.
    let tryFind (person: UserId) (connection: ConnectedService) (Open asks) : OfflineAccessAsked option =
        asks |> List.tryFind (same person connection)

    /// The open asks addressed to this reader. Only the person asked sees the card: the
    /// grant is theirs to give, and everybody else reads the ask on the timeline.
    let addressedTo (reader: ActorRef) (Open asks) : OfflineAccessAsked list =
        asks |> List.filter (fun a -> UserRef a.Person = reader)
