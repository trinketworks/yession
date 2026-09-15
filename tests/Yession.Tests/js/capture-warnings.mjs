// Puts a recorder in `console.warn`'s place and hands back both what it was told and the
// way back. A module rather than an `[<Emit>]` string because it declares bindings, and an
// emit pastes them into whatever scope the call sits in, where `original` and `said` may
// already mean something (YES003).

export default (() => {
  const original = console.warn
  const said = []
  console.warn = (...parts) => { said.push(parts.join(' ')) }
  return { said, restore: () => { console.warn = original } }
})
