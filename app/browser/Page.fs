/// The document this bundle is running in, as far as addresses are concerned.
///
/// Every route the client fetches is relative (`SessionRoute.relative`), and the only thing
/// making those resolve under a path-mounted session is the `<base href>` the shell wrote —
/// so the client's proof of a base is the tag itself, read once from the page. A page without
/// one is not a shell this bundle can run in, and it says so on the first address it renders
/// rather than 404ing on the first fetch: the address is right or the boot is wrong, and
/// nothing in between.
///
/// Lazy, because the bundle has more than one entry (the editor harness runs without a
/// shell), and a module that failed at load would take those down for a tag they never use.
module Yession.Browser.Page

open Fable.Core
open Yession.App

[<Emit("document.querySelector('base')?.getAttribute('href') ?? undefined")>]
let private baseHref () : string option = jsNative

let private documentBase : Lazy<DocumentBase> =
    lazy
        (match DocumentBase.declared (baseHref ()) with
         | Ok declared -> declared
         | Error reason -> failwith reason)

/// A route as this page spells it — the form the browser resolves against the shell's base.
let href (route: SessionRoute) : string =
    RelativeUrl.inDocument documentBase.Value (SessionRoute.relative route)
