namespace Fable.BrowserExtras

// Narrowing what an event says it reached.
//
// `event.target` is an `EventTarget`: an element, the document, the window, a text node, an
// XHR. A listener that wants to ask an element's questions (`matches`, a box to measure) has
// to find out which it got first, and F#'s own type test cannot ask — `:?` against a
// `Fable.Browser.Dom` interface has no runtime class to test against. The browser has one,
// and `instanceof` is its test, so the narrowing is written here once over that test rather
// than as a cast at each listener, where a wrong guess is an `undefined` method call on
// whatever the event really reached.

open Fable.Core
open Browser.Types

[<RequireQualifiedAccess>]
module EventTargets =

    // `instanceof` answers false for `null`, so neither test needs a null check in front of it.
    [<Emit("$0 instanceof Node")>]
    let private isNode (target: EventTarget) : bool = jsNative

    [<Emit("$0 instanceof HTMLElement")>]
    let private isHTMLElement (target: EventTarget) : bool = jsNative

    [<Emit("$0 instanceof HTMLInputElement")>]
    let private isHTMLInputElement (target: EventTarget) : bool = jsNative

    /// The target as a node, or `None` for one that is not (the window, a request).
    let asNode (target: EventTarget) : Node option =
        if isNode target then Some (target :?> Node) else None

    /// The target as an HTML element, or `None` for anything else — the document itself, a
    /// text node, an SVG element.
    let asHTMLElement (target: EventTarget) : HTMLElement option =
        if isHTMLElement target then Some (target :?> HTMLElement) else None

    /// The target as an `<input>`, or `None` for anything else — a button, a textarea, the
    /// document. Whether the input carries a caret is the input's own question (`selectionStart`
    /// is `null` for a checkbox or a number field), asked of what this returns.
    let asHTMLInputElement (target: EventTarget) : HTMLInputElement option =
        if isHTMLInputElement target then Some (target :?> HTMLInputElement) else None
