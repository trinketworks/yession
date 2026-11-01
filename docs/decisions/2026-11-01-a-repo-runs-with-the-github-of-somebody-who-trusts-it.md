# A repo runs with the GitHub of somebody who trusts it

> Decided 2026-11-01 · Related:
> [2026-11-01-records-are-a-third-family-of-the-store.md](2026-11-01-records-are-a-third-family-of-the-store.md),
> [2026-09-12-a-session-chooses-its-repo-from-inside.md](2026-09-12-a-session-chooses-its-repo-from-inside.md),
> [technical-design.md §5](../technical-design.md#5-architectural-invariants) rule 25

## Context

A repository's `yession.yaml` selects the operator's resources, and a resource can forward a
connection. A repo whose sandboxes forward GitHub runs its own code (its setup, its scripts, its
hooks) with somebody's GitHub. The person whose GitHub that is had to agree.

They agreed through an approval. The fold held the sandboxes until "somebody in this session"
approved the exact set of lines the repo asked for. That had three faults.

- **It asked again in every session.** The approval was an event in one session's log. A person
  who opened the same repo the next day was asked the same question.
- **The person who approved was not the person whose GitHub was spent.** Any person in the session
  could approve. The sandboxes then started on the authority of whoever had triggered the fold, so
  Bob's yes could start a setup that spent Nick's GitHub.
- **It asked the wrong question.** A person reads "approve this list of grants". What they decide is
  whether they trust this repository's code with their GitHub.

## Decision

Trust is a person's, about one repository, and it holds across their sessions. That this session
runs a repository on somebody's trust is a fact about this session.

- **A person's trust is their own record** (`RepoTrust`, kept as a record:
  [ADR](2026-11-01-records-are-a-third-family-of-the-store.md)). It holds the routes they granted
  (`git`, `api`) and every route they were asked about. It is named by GitHub's id for the
  repository (`repo-trust/v1/github/<id>`), which a rename or a transfer keeps. A new repository
  created under an old name has a new id, and inherits no trust.
- **A loan is the capability.** A block in a repo's sandbox is lent a person's GitHub only by the
  routes the sandbox forwards that their own record grants (`RepoTrust.granted`, `GrantedRoutes`).
  The record is read for each block. The loan is their own connection only: never the session's
  "This session only" credential, the deployment's on their behalf, or the ambient `GITHUB_TOKEN`.
  So what is spent is the operator's offer ∩ the repo's ask ∩ the person's grant, and the owner of
  the grant is the owner of the token.
- **Who a repo runs with here is an event**, `RepoTrusted { Repo; By }`. Only an act of that person
  writes it: adding the repo when their record already answers everything it asks, or answering its
  card (`FoldCause.Offered`). Joining the session, a boot, and a branch switch only read who it is.
  The fold starts the repo's sandboxes on that person's authority, so their setup is lent that
  person's GitHub.
- **A repo nobody's trust covers waits**, and the fold says so once (`RepoTrustAsked`, with the
  routes). Every reader then has a card: "Do you trust this repo? Its code, on any branch or pull
  request, runs in its sandboxes with your GitHub", a checkbox per route, Trust and Not now.
- **Anyone here may answer.** The answer is written to their own record, and the repo then runs with
  their GitHub. A repo that already runs with somebody's keeps them.
- **An answer is about the routes the card showed.** A route left unticked is answered no and does
  not ask again. Only a route the repo newly asks for asks again, so a repo that widens what it
  forwards asks, and one that narrows it does not.
- **The person a repo runs with may be away.** After a restart the session cannot read their record
  until they sign in to it again. The repo then waits for them without a card, and resumes when they
  are back. A card would hand it to whoever came first.
- **The Manager learns nothing new.** It stores each record as text and never reads it. It does not
  know about GitHub, repositories, routes or grants.

## Alternatives rejected

**Keeping a per-session approval of the lines.** That is what this replaces, for the three reasons
above.

**A grant ledger in the Manager, checked again when it resolves a credential.** Rejected in the
[records ADR](2026-11-01-records-are-a-third-family-of-the-store.md). The session holds every token
it spends, so a second check in the Manager could not stop a session that misdescribed a spend.

**Letting a person who joins start a repo they already trust.** A repo's sandbox runs the session's
checkout, and the people already in the session direct the agent that edits it. Trusting a
repository is not trusting every session it is open in. If joining started it, opening a link would
spend your GitHub on code somebody else wrote there.

**Keying trust by `owner/name`.** A deleted or renamed repository's name can be taken by someone
else, and the new repository would inherit the old one's trust.

**A per-session grant store beside the log.** The log already holds the facts of one session, and it
outlives an idle-out. A second copy would have to agree with it about removals, re-adds and
restarts.

## Consequences

- Approvals given before this change are not imported. Each person is asked once per repository,
  and then not again in any session.
- `RepoCapabilitiesApproved` is retired. A log that holds one still loads, and nothing appends it.
- A person with no GitHub of their own cannot trust a repository. A repo that forwards GitHub waits
  until somebody who has one answers.
- Rule 25 of §5 says this.
- What this does not cover is in [GAPS](../GAPS.md#security--trust): the agent's own sandbox, taking
  trust back, pull requests from forks, the card's place, and a truster who does not come back.
