module Yession.Host.Ssr

// Our own dependency-free server-side renderer for Fable.Lit templates. A lit-html
// `TemplateResult` is `{ strings, values }`: the static markup parts interleaved with the
// hole values. Rendering to a string is just that interleave — recursing into nested
// templates and arrays, escaping text, and dropping the bindings a string can't carry
// (event/property/boolean holes, `@ . ?`). Because it never parses HTML, it handles
// `<textarea>` child-text bindings that @lit-labs/ssr miscounts, and because it pulls no
// dependencies it bundles trivially into the shipped single-file executable — no CDN, no
// parse5. The browser renders the same templates live; this is the first paint.

open System.Text.RegularExpressions
open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.App
open Lit

// --- lit-html TemplateResult shape (read-only) ------------------------------------------

/// A lit-html `TemplateResult` as it is shaped at runtime: the static parts, the hole values
/// between them, and the brand lit-html puts on one (`1` for `html`, `2` for `svg`). The brand
/// is what tells a template from any other object in a hole, and it is an option because on
/// anything that is not a template it is absent.
[<AllowNullLiteral>]
type private LitTemplate =
    abstract strings : string[]
    abstract values : obj[]
    abstract ``_$litType$`` : int option

/// The binding syntax lit-html reads off the end of a static part — `@x=`, `.x=`, `?x=`, with
/// or without the opening quote of the value. These carry a listener/property a string cannot.
let private binding = Regex "[@.?][A-Za-z0-9_-]+=\"?$"

/// The same, with the whitespace that set it off: what goes when the binding goes.
let private bindingWithSpace = Regex "\\s*[@.?][A-Za-z0-9_-]+=\"?$"

/// A static part ending in an attribute-value hole (`name=` or `name="`).
let private attributeHole = Regex "=\"?$"

/// Whether a value is iterable — a JS array, a Fable list, a lazy `seq`. The one question
/// here that stays a macro: Fable cannot type-test `seq<_>` (`:? seq<obj>` compiles to a
/// constant false), and the template-hole rule admits any `IEnumerable`, so a lazy sequence
/// can reach a hole and has to render as its parts.
[<Emit("typeof $0[Symbol.iterator] === 'function'")>]
let private isIterable (v: obj) : bool = jsNative

/// Public for the same reason `escapeAttr` is: the Manager's own standalone pages (Plan 11's
/// `/open` landing page and its refusals) are sprintf'd rather than Lit-rendered, and they put
/// a session id and a refusal's words into TEXT. One escaper each, shared with the renderer.
let escapeText (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

/// Public because the Manager's own standalone pages need it too (Plan 11's `/open`
/// landing page): one escaper for every string this process puts in an attribute.
let escapeAttr (s: string) =
    s.Replace("&", "&amp;").Replace("\"", "&quot;")

/// One hole's value, as text. The cases are the renderable types the template-hole rule
/// admits (`TemplateHoles.fs`) — text, a template, a sequence of them, a number, a bool — and
/// then what a string cannot carry: a listener, lit's `nothing`/`noChange` sentinels, any
/// other object, all of which render as nothing. Each JavaScript kind but one is named by the
/// F# type test that compiles to it, where this used to ask `typeof` in a macro and hand a
/// number to `String()`. The order is the guard: `null` before anything a property is read
/// off, text before the iterable test a string would also pass.
let rec private renderValue (inAttr: bool) (v: obj) : string =
    match v with
    | null -> ""
    | :? string as s -> if inAttr then escapeAttr s else escapeText s
    | :? float as n -> string n
    | :? bool as b -> if b then "true" else "false"
    | candidate when (unbox<LitTemplate> candidate).``_$litType$``.IsSome ->
        renderTemplate (unbox<LitTemplate> candidate)
    // JS arrays AND Fable's F# lists, which lit-html renders as a sequence of child parts.
    | items when isIterable items -> unbox<obj seq> items |> Seq.map (renderValue false) |> String.concat ""
    | _ -> ""

and private renderTemplate (template: LitTemplate) : string =
    let strings = template.strings
    let values = template.values
    let sb = System.Text.StringBuilder ()
    for i in 0 .. values.Length - 1 do
        let s = strings.[i]
        if binding.IsMatch s then
            // Drop the binding syntax and its value — a string can't carry a listener.
            sb.Append (bindingWithSpace.Replace (s, "")) |> ignore
        else
            sb.Append s |> ignore
            sb.Append (renderValue (attributeHole.IsMatch s) values.[i]) |> ignore
    sb.Append strings.[strings.Length - 1] |> ignore
    sb.ToString ()

/// Render a Fable.Lit template to an HTML string.
let render (template: TemplateResult) : string = renderTemplate (unbox<LitTemplate> template)

/// Render the client shell for `model` to a string (the view's `ViewActions` are no-ops —
/// the handlers never fire during rendering).
let renderModel (model: ClientModel) : string =
    render (View.view ViewActions.ssr model ignore)

/// The full bootstrap document: the served page IS the client shell. Embeds the serving
/// session id (so the browser keys its local doc store before any connection), the local
/// stylesheet, and the `#app` mount whose classes persist across the browser's re-renders.
///
/// `mount` is the path this session is served under (`""` at an origin root) and it is a
/// REQUIRED parameter for a reason: it becomes the `<base href>` every relative URL in
/// the page and in the client bundle resolves against (`SessionRoute.relative` emits
/// nothing else), so a page rendered without one would ask the origin root for its own
/// assets. There is no way to render this document and forget it — `DocumentBase.declare`
/// hands back the tag and the witness together, and every relative address below is
/// rendered through that witness.
/// `assets` is the BUILD this shell is rendered against — one address covering every static
/// file the server will hand out, so the document names bytes that exist. That pairing is what
/// makes the assets cacheable forever and the shell the only thing that has to be fresh. The
/// shell names the files it links and nothing else knows they exist; a build that adds one
/// changes nothing here but the line that links it.
let page (sessionId: SessionId) (mount: string) (managerOrigin: string option) (ephemeralStorage: bool) (assets: AssetBuild) (model: ClientModel) : string =
    let declared, baseTag = DocumentBase.declare mount
    let href (route: SessionRoute) = RelativeUrl.inDocument declared (SessionRoute.relative route)
    let asset (file: AssetFile) = RelativeUrl.inDocument declared (AssetBuild.url assets file)
    String.concat "" [
        "<!doctype html>"
        // The terminals column's open state is a class on the ROOT, outside `#app`
        // (`PaneShell.setOpen`), so the first paint has to carry it: a shell that said
        // nothing painted the column open, and the client's first render then shut it — a
        // full-width pane sliding off a phone's screen, a 200ms width collapse on a desktop,
        // before anything the person asked for. The same bit the client writes, from the
        // same model field, so the first paint and the first render agree by construction.
        (if model.TerminalsOpen then "<html lang=\"en\">"
         else sprintf "<html lang=\"en\" class=\"%s\">" Dom.termClosedClass)
        "<head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        // Before the stylesheet and the bundle, because it governs how both resolve.
        baseTag
        sprintf "<meta name=\"%s\" content=\"%s\">" Dom.sessionMetaName (escapeAttr (SessionId.value sessionId))
        // Where to ask for this session back once it has stopped (Plan 11). OMITTED, never
        // emitted empty, when this session has no Manager: the client's offer to bring the
        // session back is a total function of the model, so a missing tag is what makes it
        // structurally impossible to render a button with nowhere to go.
        (match managerOrigin with
         | Some origin -> sprintf "<meta name=\"%s\" content=\"%s\">" Dom.managerMetaName (escapeAttr origin)
         | None -> "")
        // Said only when it is true, so the shell of a path-mounted deployment carries
        // nothing about storage at all.
        (if ephemeralStorage then sprintf "<meta name=\"%s\" content=\"1\">" Dom.ephemeralStorageMetaName else "")
        "<title>Yession</title>"
        Style.headTags (asset AssetFile.``app``)
        // The replay player's sheet, linked but inert: most sessions never open a recording,
        // and a second render-blocking stylesheet in the head would make all of them pay for
        // the ones that do. `Replay.mount` flips its `media` when a replay is first shown.
        Style.deferredHeadTags (asset AssetFile.``player``) Dom.playerStylesheetHook
        // What makes this installable, and chrome-less once it is (`WebApp`). Both URLs are
        // relative, like every other one here, so they resolve under the mount rather than at
        // the origin root — and the manifest's own contents then resolve against ITS address.
        WebApp.headTags (href Manifest) (href Icon)
        // The ONE inline script in the shell, and the only thing that has to run before first
        // paint: a collapsed sidebar is a stored preference (written by the nav toggle), and
        // applying it from the bundle would paint the sidebar open and then shut it. Desktop
        // only — below the breakpoint the same class means "the drawer is open" (Style.sidebar),
        // which is never a preference.
        "<script>try{if(matchMedia('(min-width: 768px)').matches"
        + "&&localStorage.getItem('yession.nav')==='collapsed')"
        + "document.documentElement.classList.add('nav-alt')}catch(e){}</script>"
        "</head><body>"
        sprintf "<main id=\"%s\" class=\"%s\">%s</main>" Dom.appId Style.app (renderModel model)
        sprintf "<script type=\"module\" src=\"%s\"></script>" (asset AssetFile.``client``)
        "</body></html>"
    ]
