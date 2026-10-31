# A2UI fixtures

Copied from [a2ui-project/a2ui](https://github.com/a2ui-project/a2ui) at commit
`b8bee8fbd16f37afae8a6ba3f1ad16a76d305130`, under its Apache 2.0 licence (`LICENSE` here).

- `v0_9_1/examples/` is `specification/v0_9_1/catalogs/basic/examples/`, unchanged.
- `v0_9_1/cases/` is `specification/v0_9_1/test/cases/`, unchanged.
- `conformance/` is `conformance/core/{data_model,functions,expressions}.yaml`, converted to
  JSON (the test suite reads JSON on both runtimes) with the repo's own `yaml` package:

  ```
  for f in data_model functions expressions; do
    node -e 'const fs=require("fs"),Y=require("yaml");const [src,dst]=process.argv.slice(1);fs.writeFileSync(dst,JSON.stringify(Y.parse(fs.readFileSync(src,"utf8")),null,2)+"\n")' \
      <a2ui>/conformance/core/$f.yaml conformance/$f.json
  done
  ```

`tests/Yession.Tests/A2ui.fs` runs the binding in `src/A2ui` against the examples and cases, and
`tests/Yession.Tests/A2uiCore.fs` runs its core against the conformance suites. To move to a
newer spec, copy and convert again and update the commit above.
