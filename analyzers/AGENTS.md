Rules for work on the F# analyzers in `analyzers/Yession.Analyzers`, which `lint` runs, and on their
fixtures.

Read a rule file's header (why the rule exists) before you change the rule or fix its finding.
Each diagnostic names the remedy. Reuse the shared readers (`Population`, `Surfaces`,
`Expressions`, `Environs`). Do not copy a walk into a rule.

## Fixtures

Every rule has a fixture at `analyzers/fixtures/<Rule>Fixture`. Its `// YES00n` markers say which
lines the rule must report. `lint` checks markers against that rule's diagnostics only, because
the two emit rules have opinions on each other's cases. The comments on `fixtures` and on the
fixture loop in `tasks.fsx` say when a fixture needs several files, and why `lint` refuses one
that does not compile.

- A new rule needs its `Code`, a fixture project (copy one), and an entry in `fixtures`.
- A fixture catches a rule gone silently blind: a loader that no longer matches the assembly name,
  a compiler upgrade that moves the typed-tree shape, a failed restore. Each reports a clean
  product.

## Closed lists

Widen the closed list of renderable types in `TemplateHoles.fs` only as its header says: add the
type to `Renderable.isRenderable`, and say in `TemplateHoleFixture` what the widening means.

## Cost

- Use `Kept.Answer` for a per-project cache. Its doc comment in `Kept.fs` says why a cache with no
  lifetime fails in `lint`'s one process.
- Declare a dependency on the project that uses it. A reference costs `lint` seconds. Do not dodge
  one with `[<Emit>]`. If a population walk grows to an hour, fix the analyzer.

## A justified exception

Where a rule's remedy does not apply, end the one line with the analyzer SDK's own comment. Say why
in the comment above it:

```
/// `EvaluateAsync` takes its argument as `object`; the key a call goes through types it.
let run (page: IPage) (name: string) (args: obj array) : Async<unit> = // fsharpanalyzer: ignore-line YES017
```

The marker silences one code on one line, never a block, a file or a project. A rule's fixture
shows that the CLI honours it. A rule that needs many markers has the wrong scope. Change the rule.

## `lint` verdicts

`lint` prints what each outcome means and what to do next. It reads its verdict from a SARIF
report, not the analyzer's exit code: a killed analyzer also exits non-zero. `Verdict`, `lint` and
`analyze` in `tasks.fsx` hold the reasoning.

## A YES000 that will not reproduce

`YES000` reports the first compile error in a file. A phantom one appears in CI alone, anchored
past the end of its line, in a file that compiles cleanly elsewhere. `lint` recognises it and
prints the known fix (`phantomHint` in `tasks.fsx`).

Every case so far was a name rebound across an option boundary (`secret : string option`, then
`secret : string`). A rename of the inner binding cleared CI. The cause is not known. Do not give a
binding two types under one name.
