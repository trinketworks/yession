# Examples

Rules for `examples/`: integrations built the way somebody OUTSIDE this repository would build them.

- **Standalone.** An example carries its own project and bundle. The npm package and the Nix
  installable do not include it.
- **Copy, do not reach.** If an example needs something the product has, copy it and own the copy.
  The duplication is the point, not a smell. A path through product internals is one no reader can
  reproduce.
- **Write it in the language of the thing it integrates.** `examples/serial` is F# through Fable and
  esbuild. `examples/jumpstarter` is a uv project with its own pytest suite. `example <name>`
  dispatches on which it finds. A provider is a server, not a plugin.
- **Dependencies go ONE way.** The suite tests the examples: `tests/Yession.Tests` references the
  F# one and spawns the Python one. Those tests are why the `Serial` and `Jumpstarter` capabilities
  exist.

Build one with `dotnet fsi tasks.fsx example <name>`.
