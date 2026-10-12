Rules for working on the F# analyzers in `analyzers/Yession.Analyzers`, which `lint` runs, and on their fixtures.

Each rule file starts with a header that says why the rule exists. Read it before you change a rule or
fix its finding. Each diagnostic also names the remedy. The shared readers (`Population`, `Surfaces`,
`Expressions`, `Environs`) have headers too. Reuse them. Do not copy a walk into a rule.

## Fixtures

Every rule has a fixture at `analyzers/fixtures/<Rule>Fixture`. Its `// YES00n` markers say which
lines the rule must report. On every run `lint` compares the markers with that rule's own diagnostics
in both directions. It counts only that rule's diagnostics, because the two emit rules read the same
macros and have opinions on each other's cases.

- A new rule needs its `Code`, a fixture project, and an entry in `fixtures` in `tasks.fsx`. Copy an
  existing fixture project.
- A rule about how many files do something has fixture cases in several files. One file can neither
  break that rule nor prove that it still sees a break.
- Fixtures stop a rule from going silently blind. Each of these faults reports a clean product:
  - a loader that no longer matches the assembly name
  - a compiler upgrade that moves the typed-tree shape
  - a project that failed to restore

## Closed lists

`TemplateHoles.fs` admits a closed list of renderable types in a Lit template hole. Widening it is
deliberate. Add the type to `Renderable.isRenderable`, and say in `TemplateHoleFixture` what the
widening means. The rule fails by silence, and a closed list fails loudly.

## Cost

- Use `Kept.Answer` for a per-project cache. `lint` analyzes every project in ONE process, and a cache
  that never evicts once grew the analyzer to 10.5 GB.
- Declare a dependency on the project that uses it. A reference costs `lint` seconds. Do not dodge one
  with `[<Emit>]`. If a population walk grows to an hour, fix the analyzer.

## `lint` exit codes

- 1: a rule rejected the source. Fix the finding.
- 2: the source does not compile, so nothing was judged. Run `build`.
- 3: the analyzer did not finish, so nothing was judged. Run `lint` again.

`lint` takes its verdict from a SARIF report that it names, not from the analyzer's exit code. A killed
analyzer prints nothing and exits non-zero like a finding. `tasks.fsx` (`lint`, `analyze`) holds the
reasoning.

## A YES000 that will not reproduce

`YES000` reports the first compile error in a file. It can appear in CI alone. It is a phantom type
mismatch, anchored at a column past the end of its line. The file compiles cleanly under `build`,
`fsharp-analyzers`, Fable and a from-scratch `lint` on this box.

Every case so far was a name shadowed across an option boundary. Example: `secret : string option`
rebound as `secret : string` in the branch that resolved it. Renaming the inner binding to
`launchSecret` cleared CI. The cause is not yet known. Until it is, do not give a binding two types
under one name. Rename the inner binding.
