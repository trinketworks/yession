# A session chooses its repository from inside; the Manager offers nothing

> Decided 2026-09-12 · Supersedes nothing · Related:
> [src/Yession.App/Launch.fs](../../src/Yession.App/Launch.fs) — the surface's state and
> when it is offered,
> [app/GitHubRepos.fs](../../app/GitHubRepos.fs) — the listing it chooses from,
> [app/Commands.fs](../../app/Commands.fs) — `launchRepo`, the act a choice becomes,
> [2026-08-29-the-manager-relays-hooks-it-cannot-read.md](2026-08-29-the-manager-relays-hooks-it-cannot-read.md)
> — why the Manager is kept ignorant, which this keeps

## Decision

Which repository a session is FOR is chosen **inside the session**, on its first screen,
by the person who opened it. The Manager's create form stays one press with no fields: it
launches an empty session, and the session's own launch surface — the repositories the
person's credential reaches, a search of the provider by name, a link pasted from the
provider's own pages — produces the one command that begins it (`AddRepo`), through the
same gate the agent's `add_repo` goes through, attributed to the person.

The Manager learns nothing new. It does not list repositories, does not hold a
provider's credential, does not carry a choice into a launch, and does not remember what
earlier sessions chose.

## What was weighed

Three shapes were on the table.

**The Manager collects and offers.** Sessions report "launch options" over the control
channel — opaque `kind/value/label` triples, the `sessionSummaryReport` posture — the
Manager renders them beside Create, and a pick rides into the launch envelope for the
session to interpret. One click on the roster for a repeat. But the first session ever has
nothing to pick; the Manager cannot browse a provider it holds no credential for; every
new dimension of a launch (a branch, a pull request) is either an opaque string the
Manager's page cannot validate — a wrong one fails inside the session, far from where it
was typed — or a shape the Manager has to learn. And a roster page showing "repo: x"
labels is a Manager that knows what a repo is in every way but the type signature.

**A launch surface inside the session.** Everything provider-shaped stays where the
provider already is: the credential, the sign-in panel, the git gateway, `GitHubPrs.fs`.
The listing is the person's own recently-pushed repositories, so "recents" cost nothing
and are never cold; search reaches anything public without the session being told its
exact name; branches come from the same place. A second forge is a second listing file,
and "start from pull request #123" is a session change the Manager never sees. What it
costs is a second press — Create, then choose — and that the picker knows nothing about
what OTHER sessions chose.

**Both: the surface, plus an opaque "recreate me" relay** the Manager stores and returns
without parsing, for a "create like…" button on the roster. Deferred rather than refused:
it is only worth building if roster-page repeat creation proves wanted once the surface
has been lived with, and the provider's own recency ordering is expected to make it moot.
Recorded here so the next person does not re-derive it.

The second shape is what was built.

## Why a human may run this one command

Plan 15 retired the Repos panel's add, remove and switch buttons: commands mutate and
belong to the agent, and a human who wants a repo added asks. That line is kept. What
changes is one act, at one moment: before any turn has run, there is nobody to ask, and
which repository the session is for is the person's to say — it is the thing they came to
say. So `AddRepo` is admitted only while the session has no repo at all; a second one is
still the agent's to add, and the surface does not come back once anything has happened.
It is the same gated `add_repo` — classified, attributed, on the timeline — with a second
caller rather than a second implementation.

## An ask card, not a form

The first cut of the surface was a wizard: choose a row, then a branch, then Start, with
Back beside it and "start without a repository" below — three stages and three buttons
over a paragraph explaining them, in a product whose every other act is one line said to
a composer. The second was a list at the foot of the timeline whose rows launched on tap;
it read as history rather than a decision, its rows did not look pressable, and thirty of
them was a page. What stands now is an **ask card**, docked above the composer where the
queue's bands dock: *who asks · the question · rows to hold · one button · a way out*.

The anatomy is deliberately general. It is the session asking which repository today, and
it is the shape the agent will use to ask a person a question with several answers. So
holding a row is a STATE and starting is a PRESS: tapping a row holds it (blue edge, a
mark, a lifted ground) and sends nothing; the held row grows a branch field where its
description was; START is the one thing that sends. Holding several rows later is a list
where an option is, not a redesign. Four rows are shown, then "N more"; a bare
`owner/name` typed into the field searches (a name half typed still parses as one, and an
exact one is looked up and answered under the provider's current name); a link copied
from the provider — a page, a `tree/<branch>` page, a `pull/<n>` page, a clone URL — is
resolved to a row and held, on the branch it named. A pull request is asked about first,
because which fork its branch lives in only the provider knows.

The branch is a field with the provider's branches to choose from, not a menu of them: a
hundred branches is a list nobody scrolls, three letters and a pick is how a person names
one, and a name the provider has not got yet is typed the same way (`switch_branch`
creates it).

The card wears the blue leading edge a queued command wears, because it means the same
thing there: waiting on you. ✕ dismisses it for this client; a session that is not about
a repository begins the way it always did, by saying something.

## What the surface promises, and what it does not

It is offered only to a client that is connected, has read the log to where the session
says it ends, and found no repo and nothing said. A client still reading has an empty
projection too, and a launch screen that flashed over every cold open of an old session
would teach people it means nothing.

A repository chosen from it is named as the provider names it. That is not a nicety: a
clone follows a renamed repository's old name through the provider's redirect and keeps an
`origin` the provider no longer answers to by that name, and the session that prompted all
this had done exactly that on a guess. The listing is the provider's, so a stale name
cannot be picked; `add_repo` asked by name now checks the same fact before it clones.

It does not choose a sandbox. What the checkout declares is what runs, as it would for a
repo the agent added.

## What this costs

A person's command has no tool result for a failure to come back in, so the log gained a
sibling of `CommandRefused`: `GatedCommandFailed`, recorded by the Host for a launch that
ran and did not succeed. The agent reads a failure in its tool result and everyone else on
the `ToolUseFinished` line, which is why the gate records nothing for the agent's commands
and this is recorded only where nothing else would say it.

And the session's first screen is now a screen, where it used to be a caret. A session
that is not about a repository — a conversation, a terminal on this machine — begins the
way it always did, by saying something, with a list above the composer it did not ask for.
