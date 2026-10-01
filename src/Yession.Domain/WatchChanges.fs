namespace Yession.Domain.Watching

open System
open Yession.Domain
open Yession.Domain.Prs

/// What every watched thing's change has in common, whatever it is a change OF.
///
/// A watch keeps the last known state of something outside the session — a pull request
/// today — and records what moved as its own kind of event, in its own words
/// (`PrTransitioned`). Those stay per kind: a pull request's change and a deployment's are
/// different facts, read and phrased differently. What does NOT differ is the contract
/// downstream relies on — what changed, whose watch noticed, and when it HAPPENED at the
/// source as opposed to when the session wrote it down. That is this record, and
/// `ofEvent` is the one place a kind declares it keeps it: a new kind is a new case there,
/// and everything that reads the contract — the lateness a note and the agent are told,
/// and whatever comes to read it next — handles it without knowing it exists.
type WatchChanged =
    { /// What changed, as a phrase names it.
      Subject : EntityRef
      /// Whose watch noticed: whose credential looked, and who a turn it wakes runs as.
      Watcher : Principal
      /// When it happened at the source, where the source says. The envelope's timestamp is
      /// when the session noticed; the distance between the two is how late it was noticed.
      OccurredAt : DateTimeOffset option }

module WatchChanged =

    /// The contract, read off an event that keeps it. `None` for everything else.
    let ofEvent (event: SessionEvent) : WatchChanged option =
        match event with
        | SessionEvent.PrTransitioned p -> Some { Subject = EntityRef.Pr p.Pr; Watcher = p.Watcher; OccurredAt = p.OccurredAt }
        | _ -> None

/// How late a change was noticed — the distance between when it happened at the source and
/// when this session recorded it — once that is more than a look's own rhythm. Nothing
/// explains an outage in particular: a session that was stopped, a credential that could
/// not look, a rate limit that held it back all come out as the same true sentence.
module Lateness =

    /// Below this a change was noticed on time: a look every fifteen seconds, a provider
    /// that takes a moment to finish computing, and clocks that disagree by a little.
    let threshold = TimeSpan.FromMinutes 2.0

    /// How late, when it was late.
    let between (occurredAt: DateTimeOffset) (recordedAt: DateTimeOffset) : TimeSpan option =
        let late = recordedAt - occurredAt
        if late >= threshold then Some late else None

    /// What a change's sentence gains when it was noticed late.
    let phrase (late: TimeSpan) : Phrase =
        [ Segment.Text (sprintf " — happened %s before it was noticed" (Elapsed.describe late)) ]

    /// How late a recorded event was noticed, when it keeps the watch contract and was.
    let ofEnvelope (envelope: EventEnvelope<SessionEvent>) : TimeSpan option =
        WatchChanged.ofEvent envelope.Event
        |> Option.bind (fun change -> change.OccurredAt)
        |> Option.bind (fun at -> between at envelope.Timestamp)
