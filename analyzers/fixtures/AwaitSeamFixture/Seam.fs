module AwaitSeamFixture.Interop

open Fable.Core

/// The seam: the one binding allowed to await a promise raw.
///
/// The real one settles the promise first, in the tick that created it, which is what makes
/// the await below safe whenever the workflow reaches it. None of that is what the rule reads
/// — it reads which binding the await is written inside — so the settling is left out here to
/// keep the fixture to one package.
let awaitPromise (promise: JS.Promise<'a>) : Async<'a> =
    async {
        let! value = promise |> Async.AwaitPromise
        return value
    }
