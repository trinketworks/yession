module Fable.YIndexeddb

// `y-indexeddb` — the Yjs provider that keeps a document in the browser's IndexedDB, so a
// client that has seen a document once has it again before anything connects.
//
// What is declared is what this repository constructs and waits on, which is the whole of the
// provider's opening move: make one over a named store, and know when it has finished loading
// what was already there. Deliberately absent: `clearData`, `destroy` and the provider's other
// events — a store this repository never clears and a provider it never takes down (the page
// going away is what ends one) would be declarations nobody calls, which is how a binding goes
// stale unnoticed.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs in the browser.

open Fable.Core
open Fable.Core.JsInterop
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

[<Import("IndexeddbPersistence", "y-indexeddb")>]
let private persistenceClass : obj = jsNative

/// Keep `doc` in the IndexedDB store called `name`, loading whatever that store already holds
/// into it.
///
/// The receiver is parenthesised because Fable pastes the caller's TEXT for `$0`: `new $0(…)`
/// over anything but a bare identifier binds `new` to the wrong part of it (YES003).
[<Emit("new ($0)($1, $2)")>]
let private construct (ctor: obj) (name: string) (doc: Y.Doc) : IndexeddbPersistence = jsNative

let create (name: string) (doc: Y.Doc) : IndexeddbPersistence = construct persistenceClass name doc
