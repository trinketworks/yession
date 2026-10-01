module Yession.EmitInDomainFixture.Domain

open Fable.Core

/// A product project with no references is still a product project. A line marked `// YES011`
/// MUST be reported; nothing else here may be.

[<Emit("Date.now()")>] // YES011
let now () : float = jsNative

/// Plain F#, which the rule has no opinion about anywhere.
let later (at: float) = at + 1000.0
