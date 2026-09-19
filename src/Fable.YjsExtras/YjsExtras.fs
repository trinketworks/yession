module Fable.YjsExtras

// What `Fable.Yjs` leaves off, or types in a shape Yjs does not call. The bar for adding
// here is the same as `Fable.BrowserExtras`'s: the upstream binding genuinely lacks it, or
// declares it so that the compiled call cannot work — not that reaching for it here was
// quicker. Everything `Fable.Yjs` types correctly (`transact`, `observe`, `observeDeep`,
// `share`, a text's `insert`, `delete`, `length` and `toString`) is used from it directly.

open Fable.Core
open Yjs

/// A document's `update` event. Yjs raises it with the encoded update AND the origin the
/// transaction was tagged with, which is the whole question a relay asks of it — is this
/// mine to send, or one I was sent. `Fable.Yjs`'s `Doc.on` types the handler as taking one
/// array of arguments, which is not the shape Yjs calls it with, so the event is bound here.
///
/// `off` removes a listener only when handed the very function reference `on` was given —
/// a caller that keeps the handler is a caller that can forget to pass the same one, which is
/// why `Sync.fs` wraps the pair as one verb answering its own stop.
module Updates =

    [<Emit("$0.on('update', $1)")>]
    let on (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit = jsNative

    [<Emit("$0.off('update', $1)")>]
    let off (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit = jsNative
