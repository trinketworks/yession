# A session announces, and the Manager relays

> Decided 2026-11-22 · Related: `Announcement` in `src/Yession.Domain/Announcements.fs`,
> `ControlRoute.Announce`, `ControlWire.announcement` and `ControlWire.SandboxAnnouncement` in
> `src/Yession.Manager/`, `reportAnnouncements` and `registryFrameOf` in
> `app/ProcessManager.fs`, `publishAnnouncements` in `app/SessionMain.fs`

## Context

A deployment puts its own parts around a session: a proxy route, a tracker, a tunnel to a
sandbox. Each part must know a fact about the session. The registry stream
(`/sessions/stream`) gave five facts for each running session: `id`, `name`, `port`, `pid` and
`build`.

Each fact that a session told the Manager had three fixed parts. The parts were a control
route, a state in the Manager and a field in the stream. `Name`, `Status` and `Activity` are
the three examples. A fourth fact, for example "this session has these sandboxes", needed a new
route, a new state and a new field.

The Manager is the version ceiling of that design. A deployment upgrades the Manager only when
no session runs, because a Manager restart stops each session. A session upgrades at its next
start. A session with a newer build could thus not tell a host a new fact until the operator
restarted the Manager.

## Decision

A session sends the Manager a set of announcements on one control route (`Announce`). An
announcement has three parts:

- `kind` says what the announcement is about, for example `sandbox`.
- `name` says which one, among the announcements of that kind from the session.
- `data` is a JSON object. The session that announces is its only writer.

The Manager stores the set and puts it in each entry of the registry stream, in the field
`announcements`. The Manager does not read `data`. The Manager makes sure that `data` is a
JSON object and that the set is in its limits.

The set is whole each time. A new set replaces the last set of the session, and an empty set
removes it. The set belongs to one run of the session process, as the standings do. When
that process stops, the Manager removes the set.

The limits of one session are 64 announcements, 128 characters for a `kind` or a `name`, and
65536 characters of `data` over the set. A `kind` uses the characters `a` to `z`, `0` to `9`
and `-`. The Manager refuses a set past a limit with a sentence that names the limit, and
keeps the last set.

The first kind is `sandbox`. A session announces each sandbox that it has. The `name` is the
sandbox as the session writes it (`default`, `owner/repo:dev`). The `data` has `state`
(`running` or `not-running`), `backend` and `resources`. `resources` lists the names of the
operator's resources that the sandbox holds on this host.

## Why

- **Opaque data, and not a type for each kind in the Manager.** A typed kind puts the ceiling
  back: the Manager must know a kind before a session can announce it. With opaque data, a new
  kind needs a new session build and a reader in the deployment. The Manager does not change.
- **The registry stream, and not a second stream.** A deployment reads one stream today. The
  stream sends whole snapshots, so a reader that connects again has each fact. An older reader
  ignores the new field, because it reads `sessions`, `id` and `port` only.
- **One route for each kind of fact from now on.** The cost of a new fact for a host is now a
  codec in the session and a reader in the deployment.
- **Limits in the Manager.** The Manager sends each frame to each subscriber. A session with a
  defect must not be able to make each frame large.
- **`Name`, `Status` and `Activity` stay.** The Manager reads each of the three: the name goes
  into its durable record, the standings into its page, and the activity into its idle
  timeout. An announcement is for a fact that the Manager does not read.

## Consequences

- A reader in a deployment trusts a session as much as the Manager does. A session is a
  process that the Manager started, on the same host. An announcement is the word of the
  session. The Manager does not check it.
- Each change of a set sends a whole frame to each subscriber. A session sends a set only when
  the set changed.
- A reader must ignore a kind that it does not know, and a field of `data` that it does not
  know. The section "The registry stream" of `docs/deployment.md` gives the rules.
- A session sends its set on a best-effort call, as it sends its standings. When the call
  fails, the stream keeps the last set until the next change.
