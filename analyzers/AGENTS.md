Rules for work on the F# analyzers in `analyzers/Yession.Analyzers`, which `lint` runs, and on their
fixtures.

Each rule file starts with a header that says why the rule exists. Read it before you change a rule or
fix its finding. Each diagnostic names the remedy. The shared readers (`Population`, `Surfaces`,
`Expressions`, `Environs`) have headers too. Reuse them. Do not copy a walk into a rule.

## Fixtures

Every rule has a fixture at `analyzers/fixtures/<Rule>Fixture`. Its `// YES00n` markers say which
lines the rule must report. On every run, `lint` compares the markers with that rule's diagnostics in
both directions. It counts only that rule's diagnostics, because the two emit rules read the same
macros and have opinions on each other's cases. It refuses a fixture that does not compile, except
`UnjudgedFixture`. A rule's silence over code that the compiler dropped proves nothing.

- A new rule needs its `Code`, a fixture project, and an entry in `fixtures` in `tasks.fsx`. Copy an
  existing fixture project.
- A rule about how many files do something has fixture cases in several files. One file cannot
  break that rule, and it cannot prove that the rule still sees a break.
- Fixtures stop a rule from going silently blind. Each of these faults reports a clean product:
  - a loader that no longer matches the assembly name
  - a compiler upgrade that moves the typed-tree shape
  - a project that failed to restore

## Closed lists

`TemplateHoles.fs` admits a closed list of renderable types in a Lit template hole. Widen it only on
purpose. Add the type to `Renderable.isRenderable`, and say in `TemplateHoleFixture` what the
widening means. The rule fails by silence, and a closed list fails loudly.

## Cost

- Use `Kept.Answer` for a per-project cache. `lint` analyzes every project in ONE process. The
  header of `Kept.fs` says why a cache with no lifetime fails there (10.5 GB, once).
- Declare a dependency on the project that uses it. A reference costs `lint` seconds. Do not dodge one
  with `[<Emit>]`. If a population walk grows to an hour, fix the analyzer.

## A justified exception

Where a rule's remedy does not apply, end the one line with the analyzer SDK's own comment. Say why
in the comment above it:

```
/// `EvaluateAsync` takes its argument as `object`; the key a call goes through types it.
let run (page: IPage) (name: string) (args: obj array) : Async<unit> = // fsharpanalyzer: ignore-line YES017
```

The marker silences one code on one line. It never covers a block, a file or a project. A rule's
fixture shows that the CLI honours it. A rule that needs many markers has the wrong scope. Change the
rule.

## `lint` verdicts

`lint` prints what each outcome means and what to do next. It takes its verdict from a SARIF report
that it names, not from the analyzer's exit code. A killed analyzer prints nothing and exits non-zero,
as a finding does. `lint` and `analyze` in `tasks.fsx` hold the reasoning.

## A YES000 that will not reproduce

`YES000` reports the first compile error in a file. A phantom one appears in CI alone, anchored
past the end of its line, in a file that compiles cleanly everywhere else. `lint` recognises it and
prints the known fix (`phantomHint` in `tasks.fsx`).

Every case so far was a name shadowed across an option boundary: `secret : string option` rebound
as `secret : string`. A rename of the inner binding to `launchSecret` cleared CI. The cause is not
known. Do not give a binding two types under one name.
