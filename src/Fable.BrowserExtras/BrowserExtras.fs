namespace Fable.BrowserExtras

// Bindings for the browser's `ResizeObserver`: a callback when an element's BOX changes,
// however it changed — a stylesheet, a parent's layout, a custom property, the window. That
// last clause is the whole reason to want one, because it is exactly the set of changes no
// render loop can see: nothing was dispatched, so nothing re-measured.
//
// Declared here rather than reached for with an `[<Emit>]` at the call site for the reason
// every binding in this repository is: an emit body is inlined into whatever calls it, so it
// is invisible to a reader of that file, unreachable from any other, and — the sharp edge —
// Fable does not treat a change to one as a change to its callers. Editing an emit leaves
// every compiled caller stale, which reads as a test failing for a reason the source does not
// contain. A binding project has none of those problems: it is a module, so it recompiles what
// depends on it.
//
// Only the slice actually used is declared. `ResizeObserverEntry` carries `contentRect`,
// `borderBoxSize` and friends; nothing here reads them, because the callback's value is that
// it FIRED — what the new size is gets measured from the element the same way it is measured
// on any other pass, so a second source for it could only disagree.

open Fable.Core
open Fable.Core.JsInterop
open Browser.Types

[<AllowNullLiteral>]
type ResizeObserver =
    /// Start reporting changes to this element's box. Fires once on `observe` with the
    /// element's current size, which is a feature rather than a quirk: a caller that wants
    /// the size measured is spared arranging the first pass itself.
    abstract observe : element: Element -> unit
    /// Stop reporting changes to one element, leaving the rest observed.
    abstract unobserve : element: Element -> unit
    /// Stop reporting everything. An observer holds a strong reference to what it observes,
    /// so an observer left behind for an element that has gone is a leak — with the callback
    /// still live to fire into whatever closed over it.
    abstract disconnect : unit -> unit

[<AutoOpen>]
module ResizeObserver =

    /// `new ResizeObserver(callback)`. The callback takes the entries and the observer; this
    /// declares neither, because the answer to "which element" is "the one you observed" as
    /// long as one observer watches one element — which is how every caller here uses it, and
    /// a rule the type cannot state but the call site can keep.
    [<Emit("new ResizeObserver($0)")>]
    let create (onResized: unit -> unit) : ResizeObserver = jsNative

    /// Whether this browser has one at all. Universally supported for years, so this is not a
    /// fallback path so much as an honest answer for a headless or synthetic host: a caller
    /// that would otherwise construct `undefined` and fail on the first `observe`.
    [<Emit("typeof ResizeObserver !== 'undefined'")>]
    let isSupported () : bool = jsNative

/// Bindings for the browser's `IntersectionObserver`: told when an element crosses into or out
/// of a scrollport, rather than a scroll handler asking after every frame whether it has.
///
/// It answers a question a scroll handler cannot, which is the reason to want one. A handler
/// only ever hears about MOVEMENT, so a target that was on screen from the first paint and has
/// never been scrolled past is a case it is structurally deaf to; an observer reports the state,
/// and reports it once on `observe` whether or not anything has moved.
///
/// Only the slice used is declared. `IntersectionObserverEntry` carries `intersectionRatio`,
/// `boundingClientRect`, `time` and friends, and the constructor takes `threshold`; none of that
/// is here, because the one question asked of it is whether the target and the root overlap at
/// all. What a caller does with several entries is a test over them, and a test is F# — not a
/// clause smuggled into an emit string where no compiler and no analyzer can read it.
[<AllowNullLiteral>]
type IntersectionObserverEntry =
    /// Whether this target and the root overlap as of this callback. A crossing back OUT is
    /// reported as readily as a crossing in, so a caller that only wants arrivals has to say so.
    abstract isIntersecting : bool

[<AllowNullLiteral>]
type IntersectionObserver =
    /// Start reporting this element's crossings. Fires once immediately with where the element
    /// stands, which is the whole point of the type rather than a quirk of it: something already
    /// on screen is reported as being on screen.
    abstract observe : element: Element -> unit
    /// Stop reporting everything. An observer holds its root and its targets, so one left behind
    /// for an element that has gone is a leak — with the callback still live to fire into
    /// whatever closed over it.
    abstract disconnect : unit -> unit

[<AutoOpen>]
module IntersectionObserver =

    /// `new IntersectionObserver(callback, { root, rootMargin })`. The callback takes the entries
    /// and the observer; this declares only the entries, for the same reason `ResizeObserver`
    /// declares neither — the answer to "which observer" is "the one you made".
    ///
    /// `root` is the scrollport the overlap is measured against, which is the card's own scroller
    /// whenever what scrolls is inside the page rather than the page itself. `rootMargin` grows
    /// that box in CSS margin syntax (`"400px 0px"`), so a target counts as on screen while it is
    /// still that far outside it — which is how a caller asks for what is coming rather than for
    /// what has arrived.
    [<Emit("new IntersectionObserver($0, { root: $1, rootMargin: $2 })")>]
    let create
        (onCrossed: IntersectionObserverEntry[] -> unit)
        (root: Element)
        (rootMargin: string)
        : IntersectionObserver =
        jsNative

/// The slice of the CSSOM the shell writes its layout through, which `Fable.Browser.Dom` does
/// not type: it stops at the DOM, and `element.style` belongs to the CSS bindings this
/// repository does not otherwise need.
///
/// Lengths the stylesheet owns, in both directions. The shell keeps its one layout number —
/// the width of the terminals column — as `--term-w` on the root element rather than in the
/// model, because it is presentation: a Lit re-render must not fight it, and a number of pixels
/// is not a fact about the session. The other direction is the same rule read backwards: what a
/// box is padded by is the stylesheet's to say, and code that measures a box must ask rather
/// than carry its own copy of the number.
[<AutoOpen>]
module Css =

    /// Set a custom property on an element. `value` carries its own unit — `"420px"`, not `420`.
    [<Emit("$0.style.setProperty($1, $2)")>]
    let setStyleProperty (element: Browser.Types.HTMLElement) (name: string) (value: string) : unit =
        jsNative

    /// Read a custom property back off an element's own inline style — what was SET, not what
    /// was computed. Empty when it is not set here, which is the honest answer: a property this
    /// element does not carry is not a number to fall back on.
    [<Emit("$0.style.getPropertyValue($1)")>]
    let styleProperty (element: Browser.Types.HTMLElement) (name: string) : string = jsNative

    /// Read a property as the cascade RESOLVED it — the design token, whichever stylesheet
    /// defined it. The counterpart to the one above: that answers "did anybody set this here",
    /// this answers "what is it".
    ///
    /// `getPropertyValue` is the CSSOM's general accessor, so this reads an ordinary property
    /// (`"padding-left"`, answered in pixels) as readily as a custom one. Both callers here
    /// want the same thing from it — a length the stylesheet decided, that no F# may assume.
    [<Emit("getComputedStyle($0).getPropertyValue($1)")>]
    let computedProperty (element: Browser.Types.HTMLElement) (name: string) : string = jsNative

/// Scrolling something into view, where the ALIGNMENT matters.
///
/// `Browser.Dom`'s `scrollIntoView ()` takes no options and therefore always aligns to the
/// start of the scrollport. That is the wrong end whenever the scrollport pins anything at
/// its top: the thing scrolled to arrives underneath it.
[<AutoOpen>]
module Scrolling =

    /// Scroll an element to the MIDDLE of its scrollport.
    ///
    /// Two reasons over the top: what is pinned at the top of a scrollport covers whatever
    /// aligns there — the timeline pins the author line, so a jump landed its target under
    /// the very line saying who was speaking — and a moment somebody has come back to is
    /// worth showing with the moments around it, which is most of why they came back.
    [<Emit("$0.scrollIntoView({ block: 'center' })")>]
    let scrollIntoMiddle (element: Browser.Types.HTMLElement) : unit = jsNative

/// What a key event is, beyond the keystroke: whether an IME was mid-composition when it
/// arrived.
///
/// `Fable.Browser.Dom`'s `KeyboardEvent` stops at the key and the modifiers, so this is the
/// one part of the event a handler cannot ask about — and it is the part that decides whether
/// an Enter is a person committing a candidate word or a person finishing a line.
[<AutoOpen>]
module KeyboardComposition =

    /// Whether this key event was dispatched while an input method editor was composing. True
    /// for every keystroke a candidate word is being assembled from, INCLUDING the Enter that
    /// accepts one — which is why a binding that acts on Enter has to ask.
    [<Emit("$0.isComposing")>]
    let isComposing (event: Browser.Types.KeyboardEvent) : bool = jsNative

/// Asking the browser what the STYLESHEET thinks, rather than deciding it again in F#.
///
/// A layout that changes at a breakpoint has two readers — the stylesheet, and whatever
/// script has to behave differently on each side of it — and the one thing they must never do
/// is each decide for themselves. A script comparing `innerWidth` to 768 is a second
/// definition of the breakpoint that disagrees with the first whenever a scrollbar, a zoom or
/// a rounded viewport gets between them.
[<AutoOpen>]
module Media =

    /// Whether a media query matches right now. The query is the stylesheet's own, passed in
    /// by the caller that shares a breakpoint with it.
    [<Emit("window.matchMedia($0).matches")>]
    let mediaMatches (query: string) : bool = jsNative

/// The one write this repository makes to the system clipboard.
///
/// `Fable.Browser.Dom`'s `Navigator` stops at the navigator's older surface, and the async
/// clipboard is not on it. Only the write is declared, because only the write is made: reading
/// somebody's clipboard is a permission prompt this product has no reason to raise.
///
/// Asking whether there IS a clipboard is half the binding, and the half no caller may skip.
/// The API is absent outside a secure context — most commonly a session reached over plain
/// HTTP at a LAN address — and reaching through an absent `navigator.clipboard` throws where a
/// refusal would have rejected, which is a fault no handler on the promise can see.
[<AutoOpen>]
module Clipboard =

    /// Whether this context has a clipboard at all.
    [<Emit("!!navigator.clipboard")>]
    let hasClipboard () : bool = jsNative

    /// `navigator.clipboard.writeText`: resolved once the write has happened, rejected when
    /// the browser refused it — a denied permission, a page that was not the foreground one.
    [<Emit("navigator.clipboard.writeText($0)")>]
    let writeClipboardText (text: string) : JS.Promise<unit> = jsNative

/// `URL.createObjectURL` and its revoke: a `blob:` address over an in-memory `Blob`, which
/// the browser fetches like any other address until the address is revoked. `Fable.Browser.Dom`
/// stops short of `URL`; the upstream binding is `Fable.Browser.Url`, a package this
/// repository does not otherwise need, and two static members are not worth the NuGet closure
/// moving for.
///
/// Revoke is the caller's: a Blob lives as long as an address to it does, so a page that
/// mints one per mount and never revokes leaks one per mount.
module ObjectUrls =

    [<Emit("URL.createObjectURL($0)")>]
    let create (blob: Blob) : string = jsNative

    [<Emit("URL.revokeObjectURL($0)")>]
    let revoke (url: string) : unit = jsNative
/// What `Fable.Browser.Dom` leaves off `Node`.
module Nodes =

    /// Whether a node is still in the document. A node replaced by a render is detached, and
    /// measuring a detached node's box says nothing.
    [<Emit("$0.isConnected")>]
    let isConnected (node: Node) : bool = jsNative

/// What `Fable.Browser.Dom` leaves off `Element`.
module Elements =

    /// Empty an element of its children, in the one call the browser does it in:
    /// `replaceChildren()` with NO arguments, which the upstream bindings do not type at all —
    /// not the replacing form either, so there is nothing here to narrow.
    ///
    /// The alternative is a loop removing the last child until there is none, which reaches the
    /// same end state through one mutation per child — every one of them a chance for anything
    /// watching the tree to observe a half-emptied element. That the browser can be told to
    /// empty it once is the whole reason to bind this rather than write the loop.
    [<Emit("$0.replaceChildren()")>]
    let clearChildren (element: Element) : unit = jsNative

/// Resolving one address against another: what the `URL` constructor is for, and what string
/// concatenation cannot be made to do. A reference may be absolute, rooted at the origin, or
/// relative to the base's DIRECTORY, and which of those it is decides how much of the base
/// survives — a question with an answer in the URL standard and no answer in an `if`.
///
/// `Fable.Browser.Dom` stops short of `URL`; `ObjectUrls` above is the other half of that same
/// absence, and says why the upstream package is not worth the closure moving for.
module Urls =

    /// `relative`, resolved against `baseAddress`, as an absolute address.
    ///
    /// The base is a parameter because WHICH base an address resolves against is a decision
    /// about the document, not a fact about resolution: a page may resolve against its own
    /// location, against a `<base href>` its shell declared, or against an address it was
    /// handed. A binding that reached for one of those itself would be answering a question it
    /// was not asked, in the one place no test can see the answer.
    [<Emit("new URL($0, $1).href")>]
    let resolve (relative: string) (baseAddress: string) : string = jsNative

    /// The path of an absolute address — everything after the authority, before any query or
    /// fragment.
    ///
    /// Absolute, and no base, because that is the question: a caller with a relative address
    /// wants `resolve` first, and one that has an absolute one has already decided what it is
    /// relative to. THROWS on an address `URL` cannot parse, which is what makes it a fact
    /// rather than a guess — a parser that answered `""` for a string that is not an address
    /// would hand a router something it would route.
    [<Emit("new URL($0).pathname")>]
    let pathname (address: string) : string = jsNative

/// One end of a `MessageChannel`.
[<AllowNullLiteral>]
type MessagePort =
    abstract onmessage : (MessageEvent -> unit) with get, set
    abstract postMessage : message: obj -> unit

/// A `MessageChannel`: two ports, and a message posted on one arrives on the other as a
/// TASK — a turn of the event loop the page may paint in, which is the one thing a
/// microtask cannot give. `Fable.Browser.Dom` stops at the DOM; the channel is in the
/// workers' bindings this repository does not otherwise need.
[<AllowNullLiteral>]
type MessageChannel =
    abstract port1 : MessagePort
    abstract port2 : MessagePort

module MessageChannel =

    [<Emit("new MessageChannel()")>]
    let create () : MessageChannel = jsNative

/// The browser's own Server-Sent Events client: a GET held open, its answer arriving as frames
/// for as long as it lives, reconnected by the browser itself when it drops and carrying the
/// page's cookies the way any same-origin request does. `Fable.Browser.Dom` stops at the DOM;
/// `EventSource` belongs to the HTML bindings this repository does not otherwise need.
///
/// Only the slice a reader of a stream uses is declared. `onerror`, `onopen` and `readyState`
/// describe the state of the connection, which nothing here acts on: the browser's own
/// reconnection IS the recovery story, so a handler layered over it could only duplicate what
/// it does or race it.
[<AllowNullLiteral>]
type EventSource =
    /// Called once per frame whose producer named no event type — which is every frame this
    /// repository's routes send. Settable rather than a `subscribe`, because that is the shape
    /// the API has: one handler, replaced by assigning another.
    abstract onmessage : (MessageEvent -> unit) with get, set
    /// Stop the connection, and stop the browser reopening it. A stream nobody closes lives as
    /// long as its document does, so whether this is called says something about the caller's
    /// lifetime rather than about the stream.
    abstract close : unit -> unit

module EventSource =

    /// `new EventSource(url)`. The connection is opened by the construction, not by a later
    /// call, so a caller with nowhere to put the frames yet is a caller that should not have
    /// made one yet.
    [<Emit("new EventSource($0)")>]
    let create (url: string) : EventSource = jsNative

/// The slice of the Cache API that a READ goes through. `Fable.Browser.Dom` types none of it —
/// it stops at the DOM, and a `Cache` belongs to the service-worker bindings this repository
/// does not otherwise need.
///
/// Only `match` and what a hit is worth asking are here. Opening a cache, enumerating its
/// addresses and writing to it are one-liners at their call site whose answers have no
/// structure to read; a hit has two — the body, and the header it was stored with — and that
/// is what earns a binding.
[<AutoOpen>]
module CacheStorage =

    /// One kept `Response`, as much of one as this repository ever reads.
    ///
    /// Nullable because a MISS is exactly that: `cache.match` answers with nothing for an
    /// address the store never held, and that is an answer rather than a fault.
    [<AllowNullLiteral>]
    type CachedResponse =
        /// The stored body, decoded as text.
        abstract text : unit -> JS.Promise<string>

    /// One request a store kept an answer for. Its address is the whole of what a reader here
    /// asks of it — the store is a bag of addresses, and what was kept AT one is read through
    /// `cacheMatch` below.
    [<AllowNullLiteral>]
    type CachedRequest =
        abstract url : string

    /// A response built HERE to be kept — never the one that came off the network, because a
    /// response carrying `redirected = true` is a known trap in the Cache API, and re-wrapping
    /// also keeps the store free of anything about how its bytes were obtained. Opaque: a
    /// caller builds one and hands it straight to `put`, and reads it back as a
    /// `CachedResponse` above.
    [<AllowNullLiteral>]
    type KeptResponse =
        interface
        end

    [<Emit("new Response($0, { headers: $1 })")>]
    let private responseCarrying (body: string) (headers: obj) : KeptResponse = jsNative

    /// `body`, as a response to keep, carrying `headers` — which the Cache API round-trips
    /// for nothing, and is where a caller puts what the bytes alone cannot say.
    let keptResponse (body: string) (headers: (string * string) list) : KeptResponse =
        responseCarrying body (createObj [ for name, value in headers -> name ==> value ])

    /// One named store.
    ///
    /// This used to be `obj`, on the grounds that nothing ever asked a `Cache` anything but
    /// `match`. The browser client's kept history asks it four things, so it is a type now,
    /// and what a reader must know about each is written where the member is rather than at
    /// whichever call site learned it.
    [<AllowNullLiteral>]
    type Cache =

        /// Every address this store holds an answer for.
        ///
        /// In INSERTION order, which is not log order: `put` of an address already kept
        /// deletes the entry and appends the new one, so an answer two tabs both fetched moves
        /// to the end. A caller that needs an order sorts by what the answers hold.
        abstract keys : unit -> JS.Promise<CachedRequest array>

        /// Keep `response` as the answer for `url`, replacing whatever was there.
        abstract put : url: string * response: KeptResponse -> JS.Promise<unit>

    /// The page's named stores.
    [<AllowNullLiteral>]
    type Stores =

        /// The store called `name`, made if this page has none by that name.
        [<Emit("$0.open($1)")>]
        abstract openStore : name: string -> JS.Promise<Cache>

        /// The names of every store this page holds.
        [<Emit("$0.keys()")>]
        abstract names : unit -> JS.Promise<string array>

    /// The page's stores, or nothing where this page has none: a document served insecurely
    /// has no `caches` at all, and reading the property would throw rather than answer.
    /// `globalThis` rather than `window`, because a bundle that also evaluates where there is
    /// no window must be able to ASK without the asking being the thing that fails.
    [<Emit("(typeof globalThis !== 'undefined' && globalThis.caches) ? globalThis.caches : undefined")>]
    let caches () : Stores option = jsNative

    /// Whether this document is a secure context — the condition every storage API here is
    /// gated on, asked the same defensive way for the same reason.
    [<Emit("(typeof globalThis !== 'undefined' && globalThis.isSecureContext === true)")>]
    let isSecureContext () : bool = jsNative

    /// `cache.match(url)` — the answer kept for one address, or null.
    [<Emit("$0.match($1)")>]
    let cacheMatch (cache: Cache) (url: string) : JS.Promise<CachedResponse> = jsNative

    /// One header off a kept response, or null when the stored response carries none — which
    /// is a store outliving the build that filled it, not an error.
    [<Emit("$0.headers.get($1)")>]
    let cachedHeader (response: CachedResponse) (name: string) : string = jsNative

/// The Storage Standard's `navigator.storage`, which no `Fable.Browser.*` package types:
/// `Fable.Browser.Navigator` carries `navigator` itself, and stops at a `// TODO: abstract
/// storage: StorageManager` — so the one member this repository asks for is declared here,
/// which is what this project is for.
///
/// Only `persist` is declared. `estimate`, `persisted` and the origin's file system are the
/// rest of the interface, and none of them is a question this client acts on: what it would
/// do differently knowing its store is kept, or how much room is left, is nothing.
[<AllowNullLiteral>]
type StorageManager =

    /// Ask for this origin's storage to be KEPT — not evicted when the browser is reclaiming
    /// room. A request rather than a guarantee: granted for an engaged site on Chrome,
    /// essentially only for an installed app on Safari, and the promise says which it was.
    /// A refusal is an ordinary answer, not a fault.
    abstract persist : unit -> JS.Promise<bool>

module PersistentStorage =

    /// The page's storage manager, or nothing where there is none to ask.
    ///
    /// Nothing covers two contexts and deliberately does not distinguish them, because a
    /// caller can do the same thing about either: a document served insecurely has no
    /// `navigator.storage` at all, and a browser old enough to carry one without `persist`
    /// cannot be asked — reaching through either would throw where a refusal would have
    /// answered false. `globalThis` rather than `window`, for the reason `caches` above gives:
    /// a bundle that also evaluates where there is no window must be able to ASK without the
    /// asking being the thing that fails.
    [<Emit("(typeof globalThis !== 'undefined' && globalThis.navigator && globalThis.navigator.storage && globalThis.navigator.storage.persist) ? globalThis.navigator.storage : undefined")>]
    let storage () : StorageManager option = jsNative
