namespace Yession.Domain.Sandboxes

open Yession.Domain

/// One entry that has not ended.
type OpenEntry =
    { OpenEntryId : EntryId
      OpenSandbox : SandboxRef
      OpenSubject : string
      OpenVia : string
      OpenCommand : string list
      /// When the command started.
      OpenSince : System.DateTimeOffset }

/// The entries of a session that are open now, folded from its log.
///
/// This is the state that "is somebody in a sandbox" reads: the busy check of the session,
/// and a surface that shows who is in a sandbox.
type EntryProjection = { OpenEntries : OpenEntry list }

module EntryProjection =

    let empty : EntryProjection = { OpenEntries = [] }

    /// Fold one event. An entry that the log starts two times is one entry, and an end with
    /// no start changes nothing, so a replay gives the same state.
    let applyEvent (proj: EntryProjection) (at: System.DateTimeOffset) (event: SessionEvent) : EntryProjection =
        match event with
        | SessionEvent.SandboxEntered entered ->
            if proj.OpenEntries |> List.exists (fun entry -> entry.OpenEntryId = entered.Entry) then proj
            else
                { OpenEntries =
                    proj.OpenEntries
                    @ [ { OpenEntryId = entered.Entry
                          OpenSandbox = entered.EnteredSandbox
                          OpenSubject = entered.EnteredAs
                          OpenVia = entered.ViaLabel
                          OpenCommand = entered.EnteredCommand
                          OpenSince = at } ] }
        | SessionEvent.SandboxLeft left ->
            { OpenEntries = proj.OpenEntries |> List.filter (fun entry -> entry.OpenEntryId <> left.LeftEntry) }
        | _ -> proj

    /// True when one entry or more is open.
    let anyOpen (proj: EntryProjection) : bool = not (List.isEmpty proj.OpenEntries)

    /// How many entries are open.
    let count (proj: EntryProjection) : int = List.length proj.OpenEntries

    /// The open entries of one sandbox, oldest first.
    let openIn (sandbox: SandboxRef) (proj: EntryProjection) : OpenEntry list =
        proj.OpenEntries |> List.filter (fun entry -> entry.OpenSandbox = sandbox)

    /// The events that close each open entry, for a session that found them open when it
    /// started. The process that held them is gone.
    let closingAll (proj: EntryProjection) : SessionEvent list =
        proj.OpenEntries
        |> List.map (fun entry ->
            SessionEvent.SandboxLeft
                { SandboxLeft.LeftEntry = entry.OpenEntryId
                  SandboxLeft.ExitCode = None
                  SandboxLeft.EndedBecause = SandboxLeft.sessionRestarted })

/// One way into a sandbox that a deployment offers now.
type OpenWay =
    { OpenWayTo : SandboxRef
      OpenWayVia : string
      OpenWayAddress : string }

/// The ways into the sandboxes of a session that a deployment offers now, folded from its
/// log. A surface reads it to tell a person how to come into a sandbox.
type WayProjection = { OpenWays : OpenWay list }

module WayProjection =

    let empty : WayProjection = { OpenWays = [] }

    let private isWay (sandbox: SandboxRef) (via: string) (way: OpenWay) : bool =
        way.OpenWayTo = sandbox && way.OpenWayVia = via

    /// Fold one event. One sandbox has one way for each label: an offer replaces the way
    /// that has the same sandbox and label, and keeps its place. A withdrawal of a way that
    /// is not open changes nothing, so a replay gives the same state.
    let applyEvent (proj: WayProjection) (event: SessionEvent) : WayProjection =
        match event with
        | SessionEvent.SandboxWayOffered offered ->
            let way =
                { OpenWayTo = offered.WayTo
                  OpenWayVia = offered.WayVia
                  OpenWayAddress = offered.WayAddress }
            if proj.OpenWays |> List.exists (isWay offered.WayTo offered.WayVia) then
                { OpenWays = proj.OpenWays |> List.map (fun held -> if isWay offered.WayTo offered.WayVia held then way else held) }
            else { OpenWays = proj.OpenWays @ [ way ] }
        | SessionEvent.SandboxWayWithdrawn withdrawn ->
            { OpenWays = proj.OpenWays |> List.filter (fun held -> not (isWay withdrawn.WithdrawnTo withdrawn.WithdrawnVia held)) }
        | _ -> proj

    /// The ways into one sandbox, oldest first.
    let openTo (sandbox: SandboxRef) (proj: WayProjection) : OpenWay list =
        proj.OpenWays |> List.filter (fun way -> way.OpenWayTo = sandbox)

    /// The events that withdraw each open way, for a session that found them open when it
    /// started. The process that held them is gone, and the deployment offers them again.
    let withdrawingAll (proj: WayProjection) : SessionEvent list =
        proj.OpenWays
        |> List.map (fun way ->
            SessionEvent.SandboxWayWithdrawn
                { SandboxWayWithdrawn.WithdrawnTo = way.OpenWayTo
                  SandboxWayWithdrawn.WithdrawnVia = way.OpenWayVia })
