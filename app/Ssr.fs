module Yession.Host.Ssr

// Our own dependency-free server-side renderer for Fable.Lit templates. A lit-html
// `TemplateResult` is `{ strings, values }`: the static markup parts interleaved with the
// hole values. Rendering to a string is just that interleave — recursing into nested
// templates and arrays, escaping text, dropping the bindings a string can't carry
// (event/property holes, `@ .`), and writing a boolean hole (`?`) as the attribute it
// toggles. Because it never parses HTML, it handles
// `<textarea>` child-text bindings that @lit-labs/ssr miscounts, and because it pulls no
// dependencies it bundles trivially into the shipped single-file executable — no CDN, no
// parse5. The browser renders the same templates live; this is the first paint.
//
// What a hole is escaped FOR is decided by where it sits in the template, and a string from
// outside the process (a session's display name, an MCP server's name) reaches HTML through
// no other door. A scanner walks the static parts in order and carries two facts across them:
// whether it is inside a tag, and whether it is inside a quoted attribute value (and which
// quote opened it). A hole is an attribute value when the scanner is inside a quoted value at
// the end of the static part before it: at the head of the value (`="{x}`) and after text in
// it (`="Stop {x}`) alike, which a test of the part's last characters missed — `"` passed
// through the text escaper and closed the attribute. A hole after an unquoted `=` is a value
// too, and the renderer writes the quotes around it. Everything else is text. A `"` between
// tags is text and moves nothing, and so is a quote in an HTML comment: `<!--` to `-->` is
// a state of its own, which only its end leaves. The binding checks come first: a `@ . ?`
// hole is not text or a value, whatever the scanner says.

open System.Text.RegularExpressions
open Yession.Domain
open Yession.App
open Lit
open Fable.LitExtras
open Fable.NodeExtras

/// The binding syntax lit-html reads off the end of a static part — `@x=`, `.x=`, `?x=`, with
/// or without the opening quote of the value. These carry a listener/property a string cannot.
let private binding = Regex "[@.?][A-Za-z0-9_-]+=\"?$"

/// The same, with the whitespace that set it off: what goes when the binding goes.
let private bindingWithSpace = Regex "\\s*[@.?][A-Za-z0-9_-]+=\"?$"

/// A BOOLEAN attribute hole (`?name=`), naming the attribute. Unlike a listener or a property,
/// this is markup a string can carry: present when the value is true, absent when it is false
/// — and it has to be in the first paint when it says what may be focused (`inert` on a shut
/// pane), or the page is wrong until the bundle has run.
let private booleanBinding = Regex "\\s*\\?([A-Za-z0-9_-]+)=$"

/// Where the scanner is at the end of a static part. Carried into the next part, because a
/// hole does not end the tag or the value it sits in.
type private Context =
    | InText
    | InTag
    | InValue of char
    | InComment

/// Walk one static part from `context`. `<` opens a tag only before what can start one (a
/// name, `/` or `!`), or at the end of the part, so `a < b` in text stays text. `<!--` opens a
/// comment instead, and only `-->` ends it: nothing inside moves the state, so an apostrophe
/// in a comment's prose opens no value. In a tag, a quote opens a value and `>` ends the tag;
/// in a value, only its own quote ends it.
let private scan (context: Context) (part: string) : Context =
    let mutable context = context
    for i in 0 .. part.Length - 1 do
        let c = part.[i]
        context <-
            match context with
            | InText when c = '<' && part.Substring(i).StartsWith "<!--" -> InComment
            | InText when c = '<' ->
                if i + 1 = part.Length then InTag
                else
                    let next = part.[i + 1]
                    if System.Char.IsLetter next || next = '/' || next = '!' then InTag else InText
            | InText -> InText
            | InTag when c = '"' || c = '\'' -> InValue c
            | InTag when c = '>' -> InText
            | InTag -> InTag
            | InValue q when c = q -> InTag
            | InValue q -> InValue q
            | InComment when c = '>' && i >= 2 && part.[i - 1] = '-' && part.[i - 2] = '-' -> InText
            | InComment -> InComment
    context

/// A hole after `name=` with no quote: a value the renderer has to quote itself.
let private unquotedValue = Regex "=\\s*$"

/// Public for the same reason `escapeAttr` is: the Manager's own standalone pages (Plan 11's
/// `/open` landing page and its refusals) are sprintf'd rather than Lit-rendered, and they put
/// a session id and a refusal's words into TEXT. One escaper each, shared with the renderer.
/// `"` too, so that text which lands in a value by a template's mistake still cannot close it.
let escapeText (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// Public because the Manager's own standalone pages need it too (Plan 11's `/open`
/// landing page): one escaper for every string this process puts in an attribute. Both
/// quotes, so it holds in a value either quote opened, and `<` `>`, so it holds in a value
/// that something later reads as markup.
let escapeAttr (s: string) =
    s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&#39;").Replace("<", "&lt;").Replace(">", "&gt;")

/// One hole's value, as text. The cases are the renderable types the template-hole rule
/// admits (`TemplateHoles.fs`) — text, a template, a sequence of them, a number, a bool — and
/// then what a string cannot carry, which renders as nothing. What KIND of value a hole holds
/// is `Fable.LitExtras`' question, answered once per hole; this only says what each kind
/// renders as.
let rec private renderValue (inAttr: bool) (value: Value) : string =
    match Hole.classify value with
    | Hole.Text s -> if inAttr then escapeAttr s else escapeText s
    | Hole.Number n -> string n
    | Hole.Flag b -> if b then "true" else "false"
    | Hole.Template parts -> renderParts parts
    | Hole.Sequence items -> items |> Seq.map (renderValue inAttr) |> String.concat ""
    | Hole.Inert -> ""

and private renderParts (parts: Parts) : string =
    let strings = parts.Statics
    let values = parts.Values
    let sb = System.Text.StringBuilder ()
    let mutable context = InText
    for i in 0 .. values.Length - 1 do
        let s = strings.[i]
        context <- scan context s
        let flag = booleanBinding.Match s
        if flag.Success then
            sb.Append (booleanBinding.Replace (s, "")) |> ignore
            match Hole.classify values.[i] with
            | Hole.Flag true -> sb.Append(" ").Append flag.Groups.[1].Value |> ignore
            | _ -> ()
        elif binding.IsMatch s then
            // Drop the binding syntax and its value — a string can't carry a listener.
            sb.Append (bindingWithSpace.Replace (s, "")) |> ignore
        else
            sb.Append s |> ignore
            match context with
            | InValue _ -> sb.Append (renderValue true values.[i]) |> ignore
            | InTag when unquotedValue.IsMatch s ->
                sb.Append('"').Append(renderValue true values.[i]).Append('"') |> ignore
            | InTag
            | InText
            | InComment -> sb.Append (renderValue false values.[i]) |> ignore
    sb.Append strings.[strings.Length - 1] |> ignore
    sb.ToString ()

/// Render a Fable.Lit template to an HTML string.
let render (template: TemplateResult) : string = renderParts (Parts.ofTemplate template)

/// Render the client shell for `model` to a string (the view's `ViewActions` are no-ops —
/// the handlers never fire during rendering).
let renderModel (model: ClientModel) : string =
    render (View.view ViewActions.ssr model ignore)

/// The Content-Security-Policy of every HTML document this process serves: the Manager's
/// pages and the session's shell. A script runs only from this origin, or inline with the
/// response's own nonce; nothing frames the page; a form posts only here.
///
/// `'wasm-unsafe-eval'` lets the session's client compile its WebAssembly, and nothing else:
/// it does not admit `eval` or a string of script.
///
/// `style-src 'unsafe-inline'` is for the `style="..."` attributes the session's templates
/// write (`View.fs`): a policy cannot allow an attribute by nonce. `script-src` has no
/// `'unsafe-inline'` and no `'unsafe-eval'`. `frame-ancestors 'none'` is the one framing
/// defence, so no response carries `X-Frame-Options` beside it. WebRTC is outside any policy;
/// the event streams and `fetch` are `connect-src 'self'`.
[<RequireQualifiedAccess>]
module ContentPolicy =

    /// A fresh nonce: 16 random bytes, base64. One per response, so a nonce read off one
    /// page names nothing on the next.
    let nonce () : string =
        (Node.Api.crypto.randomBytes 16).toString Node.Buffer.BufferEncoding.Base64

    /// The policy that admits the inline script carrying `nonce`, as the header that says it.
    let header (nonce: string) : ResponseHeader =
        ResponseHeader.ContentSecurityPolicy (
            String.concat "; " [
                "default-src 'self'"
                sprintf "script-src 'self' 'nonce-%s' 'wasm-unsafe-eval'" nonce
                "style-src 'self' 'unsafe-inline'"
                "img-src 'self' data: blob:"
                "font-src 'self'"
                "connect-src 'self'"
                "worker-src 'self'"
                "frame-ancestors 'none'"
                "base-uri 'self'"
                "form-action 'self'"
                "object-src 'none'"
            ])

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
///
/// `nonce` is the response's (`ContentPolicy`): the one inline script carries it.
let page (nonce: string) (sessionId: SessionId) (mount: string) (managerOrigin: string option) (ephemeralStorage: bool) (assets: AssetBuild) (model: ClientModel) : string =
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
        // Which build served this, from the model the page is rendered OF — so the meta the
        // client reads back and the footer this paint shows are one value, not two.
        (match model.Build with
         | Some build -> sprintf "<meta name=\"%s\" content=\"%s\">" Dom.buildMetaName (escapeAttr build)
         | None -> "")
        "<title>Yession</title>"
        Style.headTags (asset AssetFile.``app``)
        // The replay player's sheet, linked but inert: most sessions never open a recording,
        // and a second render-blocking stylesheet in the head would make all of them pay for
        // the ones that do. `Replay.mount` flips its `media` when a replay is first shown.
        Style.deferredHeadTags (asset AssetFile.``player``) Dom.playerStylesheetHook
        // What makes this installable, and chrome-less once it is (`WebApp`). Both URLs are
        // relative, like every other one here, so they resolve under the mount rather than at
        // the origin root — and the manifest's own contents then resolve against ITS address.
        WebApp.headTags (href Manifest) (href Icon) (href Favicon)
        // The ONE inline script in the shell, and the only thing that has to run before first
        // paint: a collapsed sidebar is a stored preference (written by the nav toggle), and
        // applying it from the bundle would paint the sidebar open and then shut it. Desktop
        // only — below the breakpoint the same class means "the drawer is open" (Style.sidebar),
        // which is never a preference.
        "<script nonce=\"" + escapeAttr nonce + "\">try{if(matchMedia('" + Style.wideMedia + "').matches"
        + "&&localStorage.getItem('yession.nav')==='collapsed')"
        + "document.documentElement.classList.add('nav-alt')}catch(e){}</script>"
        "</head><body>"
        sprintf "<main id=\"%s\" class=\"%s\">%s</main>" Dom.appId Style.app (renderModel model)
        sprintf "<script type=\"module\" src=\"%s\"></script>" (asset AssetFile.``client``)
        "</body></html>"
    ]
