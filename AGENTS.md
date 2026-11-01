# Agents

## Communication

Respond terse like smart caveman. All technical substance stay. Only fluff die. DEFAULT: on from
the first reply. Nobody has to switch it on.

Rules:
- Drop: articles (a/an/the), filler (just/really/basically), pleasantries, hedging
- Fragments OK. Short synonyms. Technical terms exact. Code unchanged.
- Pattern: [thing] [action] [reason]. [next step].
- Not: "Sure! I'd be happy to help you with that."
- Yes: "Bug in auth middleware. Fix:"

Switch level (default full): /caveman lite|full|ultra|wenyan
Stop: "stop caveman" or "normal mode"

Auto-Clarity: drop caveman for security warnings, irreversible actions, user confused. Resume after.

Boundaries: code/commits/PRs written normal.

Artifacts: screenshot, log, anything you look at that'd interest user — `share_artifact` it. Don't
just describe. Share, then say what it shows.

## Contributing changes

Read `.agents/skills/contributing-changes/SKILL.md` **before the first edit** of any request to
build, fix, refactor or change something. It decides how the work is split, and you cannot decide
that afterwards. It owns the process from split to green master. A deviation from the request stops
the loop. Report it to the user.

Say the shape of the work to the user before you start. Write no plan document in this repository.

## Principles

[docs/technical-design.md §1](docs/technical-design.md#1-core-principles) lists the design
principles, and [§5](docs/technical-design.md#5-architectural-invariants) the numbered
invariants that follow from them. Read both before a change that adds a component, a
dependency, an option, a test or a rule. §1 owns colocation, one mechanism per requirement,
type safety, narrowing a type across call sites, and fixing a bug at both ends.

## Bootstrap

`devenv.nix` declares the dev environment (Node 24 and .NET SDK 10), the tasks and the build
outputs. `devenv shell` puts them on PATH. Run a task with `devenv shell -- <task>`, or bare
inside the shell.

- **In a yession work sandbox** (when `YESSION_SANDBOX` has a value), the devshell exists. Run the
  tasks bare. Do NOT install devenv. Do NOT run `.claude/setup.sh`.
- **In a Claude Code container**, the SessionStart hook runs `.claude/setup.sh --hook`. Its
  comments own the rest.
- **Heavy verbs take a build slot.** A verb that prints `build slot: waiting for another
  session's build …` is waiting, not hung. Do not kill it or retry it. The comment above
  `withBuildSlot` in `tasks.fsx` owns the lock.

Never run `dotnet`, `fable` or `esbuild` directly. The tasks match tool versions and PATH to CI
(`.claude/direct-tool-hook.sh` enforces it).

## Build interface

`tasks.fsx` is the complete build interface. Its header and its verb table (`help`) say so.
devenv, the workflows and the Nix `outputs` are thin wrappers. To add a build step, add a
`Target` and its `needs`, not a call in a verb's body (the `Target` doc comment says why).

## Testing

[tests/AGENTS.md](tests/AGENTS.md) owns testing: the capabilities, running a tier this box cannot
host, and the rules for writing tests. [analyzers/AGENTS.md](analyzers/AGENTS.md) owns the F#
analyzers that `lint` runs.

- Pass the capabilities THIS box has. `help` lists them. A Claude Code container has `Browser`,
  `Chromium` and `Native`.
- `check --only "<text>"` runs only the cases whose full name contains `<text>`.
- To screenshot a UI change, use `frames --boot --still` (`--desktop` for 1280x800), never
  `chrome --headless --screenshot`. [ui-exploration](.agents/skills/ui-exploration/SKILL.md)
  owns the camera.
- As root, Chromium needs `--no-sandbox`. `frames` adds it (PR #1281).
- To script Playwright by hand, import `$PLAYWRIGHT_CORE/index.mjs` (the dev shell sets it to
  the build that matches its Chromium), and launch with `channel: "chromium"`.

## UI baseline

Read [docs/visual-design.md](docs/visual-design.md) BEFORE you change a surface. WCAG 2.0 AA is
the floor. [src/Yession.App/AGENTS.md](src/Yession.App/AGENTS.md) lists what the tests do not pin.

## Examples

Read [examples/AGENTS.md](examples/AGENTS.md) before you change an example.

## Versioning

The commit history determines the version. Every green master push publishes `1.0.0-beta.<n>`.
A PR squash-merges as its title and description, so put a bump marker (`+semver: minor`, alone
on its line) in the description, and wrap the description at 72 columns. The version comment and
`bumpOf` in `tasks.fsx` own the markers.

- A breaking change to the Manager ↔ Session API is a major bump.
- New user-facing capability is a minor bump. A bug fix is a patch. A plain `feat:` subject does
  NOT bump.

## Finding F# symbols

Never search for a bare name. F# reuses one identifier for a type, its module, a union case and
record fields. Search for the declaration form, then scope to the owning file:

```
rg '^\s*(type|module|let|and)\s+Foo\b'
```

Each `.fsproj` lists `<Compile Include>` in order, and a symbol is declared in its file or an
earlier one. Text search cannot find an inferred type. Follow the right-hand side to its
declaration, or write the type you expect and let `check` tell you.
