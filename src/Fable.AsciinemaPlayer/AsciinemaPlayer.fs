module Fable.AsciinemaPlayer

// Fable bindings to the `asciinema-player` npm package: the standard player for asciicast
// v2, which is what the Session Process records a terminal as. The binding layer only — the
// slice of its surface the replay view uses, and nothing else.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs in the browser.

open Fable.Core
open Browser.Types

/// What `create` hands back. `dispose` because a player left attached to a detached node
/// keeps its worker alive; `addEventListener` because the player's own events are how it
/// says what happened — it is not an `EventTarget`, so this is its method and not the DOM's.
type [<AllowNullLiteral>] Player =
    abstract dispose : unit -> unit
    /// The player's own events, by name: `"ended"` when playback ran off the end of the
    /// cast, `"play"`, `"pause"`, and the rest of its vocabulary.
    abstract addEventListener : name: string * handler: (unit -> unit) -> unit

/// The options the replay view sets, of the many the player takes. An option left out here
/// is one this repository never sets; an optional one is the player's default when `None`,
/// which is how the player reads an option it was not given.
///
/// `startAt` and `poster` are in the RECORDING's clock — the player maps them onto the
/// idle-compressed one itself. `poster` is one of the player's poster forms, `"npt:<t>"`
/// being the one used here.
[<RequireQualifiedAccess>]
type Options =
    { fit : string
      idleTimeLimit : int
      terminalFontFamily : string
      startAt : float option
      poster : string option }

/// `create(src, element, options)`: mount a player over the cast at `src` — a URL, which a
/// `blob:` address is — into `element`.
///
/// `asciinema-player` 3.x ships proper ESM with an `exports` map, so a named import resolves
/// — unlike `@xterm/headless`, whose CommonJS `main` forced `ImportDefault`.
[<ImportMember("asciinema-player")>]
let create (src: string) (element: Element) (options: Options) : Player = jsNative
