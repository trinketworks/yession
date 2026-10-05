namespace Fable.BrowserExtras

// The service worker's side of the platform: the global scope a worker script runs in, and the
// events it is handed. `Fable.Browser.Worker` types the PAGE's side — the container, the
// registration, a worker's state — and stops there; `Fable.Fetch` types the request and the
// response a worker passes through. What is left between them is declared here, and only the
// slice the shell's worker uses (`Yession.Browser.ServiceWorker`).
//
// The upstream types are reused rather than restated: the registration is
// `Fable.Browser.Worker`'s, the request and response are `Fable.Fetch`'s, and the stores are
// `CacheStorage`'s beside this file. So a response a worker fetched goes into a store and out
// to the browser as the one type it always was.

open Fable.Core
open Fetch.Types

/// An event whose work the browser waits for: `install` and `activate`.
[<AllowNullLiteral>]
type ExtendableEvent =
    /// Hold the event open until `work` settles. A `work` that rejects fails the event — for
    /// `install`, the whole installation.
    abstract waitUntil : work: JS.Promise<unit> -> unit

/// A request a page under this worker's scope made, offered to the worker before the network.
[<AllowNullLiteral>]
type FetchEvent =
    inherit ExtendableEvent
    abstract request : Request
    /// Answer the request with `response` instead of the network. NOT calling it is the other
    /// answer, and a real one: the browser then fetches as if no worker existed.
    abstract respondWith : response: JS.Promise<Response> -> unit

/// The pages this worker could control.
[<AllowNullLiteral>]
type Clients =
    /// Take control of every page in scope now, rather than from their next load.
    abstract claim : unit -> JS.Promise<unit>

/// `self`, where the script is a service worker.
[<AllowNullLiteral>]
type ServiceWorkerGlobalScope =
    abstract registration : Browser.Types.ServiceWorkerRegistration
    abstract clients : Clients
    /// Every store this origin holds. Not an option, unlike the page's `caches ()`: a service
    /// worker only ever runs in a secure context, which is the condition the page has to ask.
    abstract caches : CacheStorage.Stores
    /// Activate as soon as installed, rather than once every page the previous worker
    /// controlled has closed.
    abstract skipWaiting : unit -> JS.Promise<unit>

    // One member per event rather than `addEventListener` over a name, so a handler is typed
    // by the event it is given and a misspelt event is a compile error rather than a listener
    // that never fires.
    [<Emit("$0.addEventListener('install', $1)")>]
    abstract onInstall : handler: (ExtendableEvent -> unit) -> unit
    [<Emit("$0.addEventListener('activate', $1)")>]
    abstract onActivate : handler: (ExtendableEvent -> unit) -> unit
    [<Emit("$0.addEventListener('fetch', $1)")>]
    abstract onFetch : handler: (FetchEvent -> unit) -> unit

[<RequireQualifiedAccess>]
module ServiceWorkerScope =

    /// The scope this script runs in. Only a service worker may read it as one: the same
    /// `self` in a page is the window, and none of these members are there.
    [<Global("self")>]
    let current : ServiceWorkerGlobalScope = jsNative

    /// Whether `request` is a navigation — a document load, the one kind of request a worker
    /// can answer with a page.
    ///
    /// Bound rather than read off `request.mode`, because that member cannot be asked from F#.
    /// `Fable.Fetch` types it `U2<string, RequestMode>`, its `RequestMode` has no `navigate`
    /// case, and BOTH arms are a string at run time (`RequestMode` is a `StringEnum`) — so a
    /// match over the union is decided by which arm Fable tests first, not by what the mode
    /// is. It tested `RequestMode` first, and a match written against the string arm answered
    /// false for every navigation: a worker that never once answered for the shell.
    [<Emit("$0.mode === 'navigate'")>]
    let isNavigation (request: Request) : bool = jsNative
