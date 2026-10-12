# A session chooses its repository from inside; the Manager offers nothing

> Decided 2026-09-12 · Supersedes nothing · Related:
> [src/Yession.App/Launch.fs](../../src/Yession.App/Launch.fs) — the state of the surface and when
> it is offered. [app/GitHubRepos.fs](../../app/GitHubRepos.fs) — the listing it chooses from.
> [app/Commands.fs](../../app/Commands.fs) — `launchRepo`, the act that a choice becomes.
> [2026-08-29-the-manager-relays-hooks-it-cannot-read.md](2026-08-29-the-manager-relays-hooks-it-cannot-read.md)
> — why the Manager is kept ignorant, which this decision keeps.

## Context

A session is FOR a repository, and someone must say which one. The create form of the Manager is one
press with no fields.

Plan 15 retired the add, remove and switch buttons of the Repos panel. Commands mutate and belong to
the agent, and a human who wants a repo added asks. Before any turn has run, there is nobody to ask.
The person came to say which repository the session is for, so that choice is theirs to make.

## Decision

The person who opened a session chooses its repository **inside the session**, on its first screen.
The create form of the Manager stays one press with no fields. It launches an empty session.

The launch surface of the session gives the person three sources:

- The repositories that their credential reaches.
- A search of the provider by name.
- A link pasted from the pages of the provider.

A choice produces the one command, `AddRepo`, that gives the session its repository. The command
goes through the same gate as the `add_repo` of the agent, and it is attributed to the person.

The Manager learns nothing new. It does not list repositories, hold the credential of a provider,
carry a choice into a launch, or remember what earlier sessions chose.

### Why a human may run this one command

The Plan 15 line stays. One act changes, at one moment. `AddRepo` is admitted only while the session
has no repo at all. A second repo is still the agent's to add. The surface does not return once
anything happens. It is the same gated `add_repo` — classified, attributed, on the timeline — with a
second caller and not a second implementation.

### An ask card, not a form

The surface is an **ask card**, docked above the composer where the bands of the queue dock. Its
anatomy: *who asks · the question · rows to hold · one button · a way out*. The header of
`Launch.fs` states the principle: choosing is a STATE, and the one button is the only PRESS that
sends. The anatomy is deliberately general. The agent will use the same shape to ask a person a
question with several answers.

- A tap on a row holds it (blue edge, a mark, a lifted ground) and sends nothing. The held row grows
  a branch field where its description was. Holding several rows later is a list where an option is,
  not a redesign.
- The card shows four rows, then "N more".
- A bare `owner/name` typed into the field searches. A half-typed name still parses as one. An exact
  name is looked up and answered under the current name that the provider gives it.
- A link copied from the provider resolves to a row and is held, on the branch it named. The link
  can be a page, a `tree/<branch>` page, a `pull/<n>` page, or a clone URL. The card asks about a
  pull request first, because only the provider knows which fork holds its branch.
- The branch is a field with the branches of the provider to choose from, not a menu. A hundred
  branches is a list that nobody scrolls. Three letters and a pick is how a person names one. A name
  that the provider does not have yet is typed the same way (`switch_branch` creates it).
- The card wears the blue leading edge that a queued command wears, because it means the same thing
  there: waiting on you. ✕ dismisses it for this client.

### What the surface promises, and what it does not

- **Offer.** Only a client that is connected, has read the log to its end, and found no repo and
  nothing said sees the surface. A client that is still reading has an empty projection too, and a
  launch screen that flashed over every cold open of an old session would teach people that it means
  nothing. `Launch.fs` states this rule.
- **Names.** A repository chosen here carries the name that the provider gives it. A clone follows a
  renamed repository through the redirect of the provider. It then keeps an `origin` that the
  provider no longer answers to by that name. The session that prompted this decision did exactly
  that, on a guess. The listing is the provider's, so a stale name cannot be picked. `add_repo`
  asked by name now checks the same fact before it clones.
- **Sandbox.** The surface does not choose one. What the checkout declares is what runs, as it would
  for a repo that the agent added.

## Alternatives rejected

**The Manager collects and offers.** Sessions report "launch options" over the control channel, as
opaque `kind/value/label` triples (the `sessionSummaryReport` posture). The Manager renders them
beside Create. A pick rides into the launch envelope for the session to interpret. A repeat is one
click on the roster. It lost for these reasons:

- The first session ever has nothing to pick.
- The Manager cannot browse a provider for which it holds no credential.
- Every new dimension of a launch (a branch, a pull request) is an opaque string or a new shape. The
  page of the Manager cannot check an opaque string, so a wrong one fails inside the session, far
  from where someone typed it. A new shape is something the Manager has to learn.
- A roster page that shows "repo: x" labels is a Manager that knows what a repo is in every way but
  the type signature.

**Both: the surface, plus an opaque "recreate me" relay.** The Manager would store the relay and
return it without parsing it. It would serve a "create like…" button on the roster. We deferred this
and did not refuse it. It is worth building only if people want repeat creation from the roster page
after they use the surface for a while. We expect the recency ordering of the provider to make it
moot.

**Two earlier cuts of the surface.** The first was a wizard. It had three stages (choose a row,
choose a branch, press Start) and three buttons, with Back beside it and "start without a
repository" below. A paragraph explained all of that. Every other act in the product is one line
said to a composer. The second was a list at the foot of the timeline, whose rows launched on tap.
It read as history and not as a decision. Its rows did not look pressable, and thirty of them made a
page.

The surface inside the session won. Everything provider-shaped stays where the provider already is:
the credential, the sign-in panel, the git gateway, and `GitHubPrs.fs`. The listing is the
recently-pushed repositories of the person, so "recents" cost nothing and are never cold. Search
reaches anything public without being told the exact name. Branches come from the same place. A
second forge is a second listing file. "Start from pull request #123" is a session change that the
Manager never sees.

## Consequences

- **A second press.** The person presses Create, then chooses.
- **No memory across sessions.** The picker knows nothing about what OTHER sessions chose.
- **A new log event.** A command of a person has no tool result for a failure to come back in. The
  log therefore gained a sibling of `CommandRefused`: `GatedCommandFailed`. The Host records it for
  a launch that ran and did not succeed. The agent reads a failure in its tool result. Everyone else
  reads it on the `ToolUseFinished` line. For that reason the gate records nothing for the commands
  of the agent, and this event exists only where nothing else would say the failure.
- **A first screen that is a screen.** It used to be a caret. A session that is not about a
  repository (a conversation, a terminal on this machine) proceeds the way it always did, by saying
  something. It now has a list above the composer that it did not ask for.
