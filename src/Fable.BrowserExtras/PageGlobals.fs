namespace Fable.BrowserExtras

// A property a script PUBLISHES on the page's global scope, for readers outside its bundle.
//
// Some readers can see nothing else. A Playwright `evaluate`, the frames tool's recorder, a
// bench driver: each runs in the page and can read a global, and none can reach a module
// binding. So a harness's entry points and an instrument's counters go on `globalThis` by
// NAME — and that name is the whole contract between two programs nothing compiles together.
//
// Which is why each one is declared exactly once, as a typed key: the name and the type of
// what lives under it, side by side, in the file that owns them. The alternative this
// replaces was to view the whole `window` as a hand-written interface of every such property
// through an `unbox` — an assertion about the global object as a whole, made at every place
// that wanted one property, and checked nowhere.
//
// Reading answers an option, because a property nobody has published yet is absent rather
// than a value: `undefined` comes back as `None`, never as a zero somebody must remember is
// not one.

open Fable.Core

/// One property of the page's global scope, named once with the type of what it holds.
type PageGlobal<'T> = private PageGlobal of name: string

[<RequireQualifiedAccess>]
module PageGlobal =

    /// Declare the property `name` as holding a `'T`. A function of more than one argument is
    /// published as a `System.Func`/`System.Action`, because that is a JavaScript function of
    /// that many parameters and a curried F# function of that type is not.
    let named<'T> (name: string) : PageGlobal<'T> = PageGlobal name

    [<Emit("globalThis[$0]")>]
    let private lookup<'T> (name: string) : 'T option = jsNative

    [<Emit("globalThis[$0] = $1")>]
    let private assign<'T> (name: string) (value: 'T) : unit = jsNative

    /// What the page holds under this name, or `None` where nothing has been published.
    let tryGet (PageGlobal name: PageGlobal<'T>) : 'T option = lookup<'T> name

    /// Publish `value` under this name, replacing whatever was there.
    let set (PageGlobal name: PageGlobal<'T>) (value: 'T) : unit = assign name value

    /// The statement that publishes `value` under this name, as a script's TEXT — for a
    /// server to put in front of a program it serves, so the program reads with `tryGet` what
    /// only the server knew. The one way this repository hands a value to a script it did not
    /// compile per request (the service worker: `Yession.App.Codecs.WorkerConfig`).
    ///
    /// Strings only, and that is what keeps it data: the JSON text of a string is exactly a
    /// JavaScript string literal (ES2019 made the two agree on U+2028 and U+2029), so both the
    /// name and the value cross as literals `JSON.stringify` spelled, never as source anybody
    /// wrote. Whatever structure the value has is the reader's to decode.
    let script (PageGlobal name: PageGlobal<string>) (value: string) : string =
        "globalThis[" + JS.JSON.stringify name + "] = " + JS.JSON.stringify value + ";\n"
