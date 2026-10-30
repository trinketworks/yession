module Fable.YIndexeddb

// `y-indexeddb` — the Yjs provider that keeps a document in the browser's IndexedDB, so a
// client that has seen a document once has it again before anything connects.
//
// What is declared is what this repository constructs and waits on, which is the whole of the
// provider's opening move: make one over a named store, and know when it has finished loading
// what was already there; and hear about a later change it could not keep. Deliberately absent: `clearData`, `destroy` and the provider's other
// events — a store this repository never clears and a provider it never takes down (the page
// going away is what ends one) would be declarations nobody calls, which is how a binding goes
// stale unnoticed.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs in the browser.

open Fable.Core
open Yjs

/// A provider, holding one document against one IndexedDB store.
[<AllowNullLiteral>]
type IndexeddbPersistence =

    /// Settles when the provider has finished loading what the store already held, and never
    /// fires again — `once`, because a caller waiting to render the document it was given
    /// wants the FIRST sync and nothing after it.
    ///
    /// A promise rather than the event, because the event is the only way to ask and one
    /// answer is all anybody here needs: the whole of the caller's interest is "is what was
    /// kept now in this document".
    [<Emit("new Promise((resolve) => $0.once('synced', resolve))")>]
    abstract whenSynced : unit -> JS.Promise<unit>

/// The provider's class, as the one thing done with it: construct a provider.
type private PersistenceClass =
    [<EmitConstructor>]
    abstract Create : name: string * doc: Y.Doc -> IndexeddbPersistence

[<Import("IndexeddbPersistence", "y-indexeddb")>]
let private persistenceClass : PersistenceClass = jsNative

/// Put a catch around the provider's write, which is its listener on the document's `update`.
///
/// The provider writes each change from inside that listener, and the write throws there when
/// the store's connection has closed under an open page: the browser cleared or evicted the
/// site's data, or (mobile Safari) lost the connection by itself. Yjs emits `update` before it
/// has finished cleaning up the transaction, so the throw leaves the cleanup unfinished, and
/// every later transaction then skips its own: the document still changes, but no observer
/// runs and no `update` goes out again. The editor stops drawing what peers send and nothing
/// typed reaches them. The throw also stops every `update` listener after this one. Upstream
/// has no fix (yjs/y-indexeddb#42, #47).
///
/// Re-adding the listener moves it to the end of the list; nothing here depends on its place.
[<Emit("""(function (provider, failed) {
    const write = provider._storeUpdate
    provider.doc.off('update', write)
    provider._storeUpdate = (update, origin) => {
      try { write(update, origin) } catch (error) { failed(error) }
    }
    provider.doc.on('update', provider._storeUpdate)
  })($0, $1)""")>]
let private catchWrites (provider: IndexeddbPersistence) (failed: exn -> unit) : unit = jsNative

/// Keep `doc` in the IndexedDB store called `name`, loading whatever that store already holds
/// into it. A change that cannot be kept goes to `writeFailed`, and the document goes on
/// without it (`catchWrites`).
let create (name: string) (doc: Y.Doc) (writeFailed: exn -> unit) : IndexeddbPersistence =
    let provider = persistenceClass.Create (name, doc)
    catchWrites provider writeFailed
    provider
