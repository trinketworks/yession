namespace Fable.BrowserExtras

// Narrowing what a listener is handed.
//
// `addEventListener` and Lit's `@event` both hand a listener an `Event`. Which event it really
// is follows from the event's NAME, and the name is a string the compiler never connects to the
// handler: a `keydown` listener is handed a `KeyboardEvent` because the browser keeps that
// promise, and a synthetic `new Event("keydown")` breaks it. A `:?>` at the listener asserts the
// promise and checks nothing, so a wrong pairing reads `key` as `undefined` far from the cast.
//
// The browser can test the class, and `instanceof` is that test. The narrowing is written here
// once, as `EventTargets` writes it for what an event reached, so a listener asks a question that
// can answer no rather than asserting an answer.
//
// Each class comes twice. `asX` is the question, for a listener that does something either way.
// `x` wraps a handler that wants that class, so a listener reads as what it listens for:
// `@keydown={Ev(Events.key (fun pressed -> ...))}`. The wrapped handler does not run for an
// event of another class; a listener registered under the right name is never handed one.

open Fable.Core
open Browser.Types

[<RequireQualifiedAccess>]
module Events =

    [<Emit("$0 instanceof KeyboardEvent")>]
    let private isKeyboard (event: Event) : bool = jsNative

    [<Emit("$0 instanceof MouseEvent")>]
    let private isMouse (event: Event) : bool = jsNative

    [<Emit("$0 instanceof PointerEvent")>]
    let private isPointer (event: Event) : bool = jsNative

    [<Emit("$0 instanceof PageTransitionEvent")>]
    let private isPageTransition (event: Event) : bool = jsNative

    [<Emit("$0 instanceof RTCPeerConnectionIceEvent")>]
    let private isIce (event: Event) : bool = jsNative

    let private narrowed (test: Event -> bool) (event: Event) : 'T option =
        if test event then Some (event :?> 'T) else None

    let private only (narrow: Event -> 'T option) (handler: 'T -> unit) : Event -> unit =
        fun event -> narrow event |> Option.iter handler

    /// The event as a key press, or `None` for anything else.
    let asKeyboard (event: Event) : KeyboardEvent option = narrowed isKeyboard event

    /// The event as a mouse event, or `None` for anything else. A `click` is one, whether a
    /// pointer or a key made it; a `PointerEvent` is one too.
    let asMouse (event: Event) : MouseEvent option = narrowed isMouse event

    /// The event as a pointer event, or `None` for anything else.
    let asPointer (event: Event) : PointerEvent option = narrowed isPointer event

    /// The event as a `pageshow` or `pagehide`, or `None` for anything else.
    let asPageTransition (event: Event) : PageTransitionEvent option = narrowed isPageTransition event

    /// The event as an `icecandidate`, or `None` for anything else.
    let asIce (event: Event) : RTCPeerConnectionIceEvent option = narrowed isIce event

    /// A handler for `keydown`, `keyup` and their kind.
    let key (handler: KeyboardEvent -> unit) : Event -> unit = only asKeyboard handler

    /// A handler for `click`, `mousedown` and their kind.
    let mouse (handler: MouseEvent -> unit) : Event -> unit = only asMouse handler

    /// A handler for `pointerdown`, `pointermove` and their kind.
    let pointer (handler: PointerEvent -> unit) : Event -> unit = only asPointer handler

    /// A handler for `pageshow` and `pagehide`.
    let pageTransition (handler: PageTransitionEvent -> unit) : Event -> unit = only asPageTransition handler

    /// A handler for an `RTCPeerConnection`'s `icecandidate`.
    let ice (handler: RTCPeerConnectionIceEvent -> unit) : Event -> unit = only asIce handler
