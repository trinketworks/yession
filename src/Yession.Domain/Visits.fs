namespace Yession.Domain.Sandboxes

open Yession.Domain

/// One visit of a person to a sandbox, as a page shows it.
///
/// An entry is one command. A person who comes through SSH makes one entry for each
/// connection, and an editor on a remote computer opens some connections at one time. A page
/// that showed each entry would say "entered" five times for one person. A visit is thus the
/// entries of one subject, in one sandbox, through one label, that are open at the same time.
/// It starts with the first entry and ends when the last one ends.
type SandboxVisit =
    { /// The offset of the event that started the visit. It is the name of the visit, and the
      /// place of its line on a page.
      VisitOffset : EventOffset
      VisitedSandbox : SandboxRef
      /// Who came, as the caller of the door said it (`SandboxEntered.EnteredAs`).
      Visitor : string
      /// How the visitor came, as the deployment labels it (`SandboxEntered.ViaLabel`).
      VisitorCame : string
      VisitStarted : System.DateTimeOffset
      /// The entries of the visit that are open, oldest first.
      VisitEntries : EntryId list }

/// The end of a visit: its last entry ended.
type VisitEnd =
    { EndedVisit : SandboxVisit
      /// The offset of the event that ended the last entry.
      VisitEndOffset : EventOffset
      VisitEnded : System.DateTimeOffset
      /// The exit code of the last entry, when it had one.
      LastExitCode : int option
      /// Why the last entry ended, in words (`SandboxLeft.EndedBecause`).
      LastReason : string }

/// What one event did to the visits.
type VisitStep =
    /// The first entry of a visit started.
    | VisitBegan of SandboxVisit
    /// The last entry of a visit ended.
    | VisitWent of VisitEnd

/// The words of a visit.
module SandboxVisit =

    /// "nick is here through ssh", for a row that already names the sandbox.
    let here (visit: SandboxVisit) : string = sprintf "%s is here through %s" visit.Visitor visit.VisitorCame

/// The visits that are open now, folded from the log.
type VisitProjection = { OpenVisits : SandboxVisit list }

module VisitProjection =

    let empty : VisitProjection = { OpenVisits = [] }

    let private holds (entry: EntryId) (visit: SandboxVisit) : bool = List.contains entry visit.VisitEntries

    /// Fold one event. The answer is the new state, and what the event did to a visit when
    /// it started one or ended one.
    ///
    /// An entry that joins an open visit starts nothing, and an entry that leaves a visit
    /// with other entries open ends nothing. An entry that the log starts two times is one
    /// entry, and an end with no start changes nothing, so a replay gives the same state.
    let applyEvent
        (proj: VisitProjection)
        (offset: EventOffset)
        (at: System.DateTimeOffset)
        (event: SessionEvent)
        : VisitProjection * VisitStep option =
        match event with
        | SessionEvent.SandboxEntered entered ->
            if proj.OpenVisits |> List.exists (holds entered.Entry) then proj, None
            else
                let same (visit: SandboxVisit) : bool =
                    visit.VisitedSandbox = entered.EnteredSandbox
                    && visit.Visitor = entered.EnteredAs
                    && visit.VisitorCame = entered.ViaLabel
                if proj.OpenVisits |> List.exists same then
                    let joined : SandboxVisit list =
                        proj.OpenVisits
                        |> List.map (fun (visit: SandboxVisit) ->
                            if same visit then { visit with VisitEntries = visit.VisitEntries @ [ entered.Entry ] }
                            else visit)
                    { OpenVisits = joined }, None
                else
                    let visit : SandboxVisit =
                        { VisitOffset = offset
                          VisitedSandbox = entered.EnteredSandbox
                          Visitor = entered.EnteredAs
                          VisitorCame = entered.ViaLabel
                          VisitStarted = at
                          VisitEntries = [ entered.Entry ] }
                    { OpenVisits = proj.OpenVisits @ [ visit ] }, Some (VisitBegan visit)
        | SessionEvent.SandboxLeft left ->
            match proj.OpenVisits |> List.tryFind (holds left.LeftEntry) with
            | None -> proj, None
            | Some visit ->
                let remaining = visit.VisitEntries |> List.filter (fun entry -> entry <> left.LeftEntry)
                if List.isEmpty remaining then
                    let ended : VisitEnd =
                        { EndedVisit = { visit with VisitEntries = [] }
                          VisitEndOffset = offset
                          VisitEnded = at
                          LastExitCode = left.ExitCode
                          LastReason = left.EndedBecause }
                    let others : SandboxVisit list =
                        proj.OpenVisits |> List.filter (fun (other: SandboxVisit) -> other.VisitOffset <> visit.VisitOffset)
                    { OpenVisits = others }, Some (VisitWent ended)
                else
                    let kept : SandboxVisit list =
                        proj.OpenVisits
                        |> List.map (fun (other: SandboxVisit) ->
                            if other.VisitOffset = visit.VisitOffset then { other with VisitEntries = remaining }
                            else other)
                    { OpenVisits = kept }, None
        | _ -> proj, None

    /// The open visits of one sandbox, oldest first: who is in it now.
    let openIn (sandbox: SandboxRef) (proj: VisitProjection) : SandboxVisit list =
        proj.OpenVisits |> List.filter (fun visit -> visit.VisitedSandbox = sandbox)
