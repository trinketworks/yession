namespace Fable.BrowserExtras

// Bindings for changing what a page's own APIs do — for an instrument installed before the
// page's scripts run, which has to stand between the page and the browser to see what passes.
// The browser cases use them (`app/browser/Shims.fs`) to take a step away from a handshake, to
// hear when one lands, to catch a DOM state in the instant it first appears, and to answer a
// media query Chromium cannot be made to answer.
//
// Each is a real function over its arguments rather than a statement pasted into its caller,
// so nothing it declares can meet a name the caller has.

open Fable.Core
open Browser.Types

/// A `MutationObserver` that has been started; disconnect it to stop it.
[<AllowNullLiteral>]
type TreeWatch =
    abstract disconnect : unit -> unit

[<RequireQualifiedAccess>]
module PagePatches =

    /// Replace the global constructor `name` with one that builds the original and hands each
    /// instance to `made` before the page sees it. The replacement keeps the original's
    /// prototype, so `instanceof` and every static still answer as before.
    ///
    /// `made` takes the instance as the type the caller names, which is the type of what the
    /// constructor `name` builds: the two are one fact, said once at the call, and this is the
    /// binding that turns a global looked up by name into a typed value.
    [<Emit("""(function (name, made) {
        const Native = globalThis[name]
        const Patched = function (...args) { const built = new Native(...args); made(built); return built }
        Patched.prototype = Native.prototype
        Object.setPrototypeOf(Patched, Native)
        globalThis[name] = Patched
      })($0, $1)""")>]
    let constructed<'instance> (name: string) (made: 'instance -> unit) : unit = jsNative

    /// Wrap the asynchronous method `methodName` of the global class `className` so `after`
    /// runs once the original's promise has settled successfully, for every instance.
    [<Emit("""(function (className, methodName, after) {
        const prototype = globalThis[className].prototype
        const original = prototype[methodName]
        prototype[methodName] = async function (...args) {
          const result = await original.apply(this, args)
          after()
          return result
        }
      })($0, $1, $2)""")>]
    let afterEach (className: string) (methodName: string) (after: unit -> unit) : unit = jsNative

    /// Call `changed` on every change under `target` — children, subtrees and attributes —
    /// synchronously with the change, before the frame that would paint it.
    [<Emit("""(function (target, changed) {
        const watch = new MutationObserver(() => changed())
        watch.observe(target, { subtree: true, childList: true, attributes: true })
        return watch
      })($0, $1)""")>]
    let watchTree (target: Node) (changed: unit -> unit) : TreeWatch = jsNative

    /// The page's own `matchMedia`, bound to the window it belongs to — for a replacement to
    /// fall through to, since `window.matchMedia` will by then be the replacement.
    [<Emit("globalThis.matchMedia.bind(globalThis)")>]
    let matchMediaAsIs () : string -> MediaQueryList = jsNative

    /// Replace `window.matchMedia`.
    [<Emit("globalThis.matchMedia = $0")>]
    let replaceMatchMedia (answer: string -> MediaQueryList) : unit = jsNative
