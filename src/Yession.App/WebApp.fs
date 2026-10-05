namespace Yession.App

/// The shell as an INSTALLABLE app — the web manifest, the icon, and the head tags that make
/// a browser offer "add to home screen" and then launch this without its chrome.
///
/// Why it is here and not in a static file: a session does not necessarily own the root of
/// its origin (an operator's proxy may mount it under a path — Plan 09), so both the
/// manifest and every URL inside it have to be relative, and the icon has to come from a
/// process that may be serving from a store path with no assets directory beside it. The
/// icon is therefore a constant in the binary, like every other thing the shell needs to be
/// self-contained (`Style.headTags`, the inline nav script): no CDN, no sidecar file.
///
/// What each tag is for, because they overlap and it is easy to add a fourth that says the
/// same thing again:
///   * `manifest` — the standard declaration. `display: standalone` is what drops the
///     browser's chrome, and Android/desktop Chrome read it to offer an install.
///   * `apple-mobile-web-app-capable` — the SAME statement to a browser that does not read
///     the manifest (iOS before 16.4). Not a fallback beside a primary: it is the only place
///     those versions look, and the manifest is the only place newer ones do.
///   * `apple-mobile-web-app-status-bar-style: black` — the status bar over a black app. The
///     `black-translucent` variant would put the app UNDER the clock, which then needs
///     safe-area insets on every fixed panel; `black` keeps the system bars as system bars.
///   * `theme-color` — what the browser tints its own bars with BEFORE anyone installs
///     anything, which is most of the value on a phone: the address bar stops being a light
///     slab over a black product.
module WebApp =

    /// `#000` said the way each consumer wants it. The manifest takes JSON, the meta tag takes
    /// an attribute, and neither can read `--color-bg` — this is the one place the ground's
    /// hex is repeated outside `app/tailwind.css`, because it is the one place a stylesheet
    /// cannot reach.
    let private ground = "#000000"

    /// The app icon and the tab's mark are `Brand.iconPngBase64` and `Brand.faviconSvg`:
    /// generated from `assets/logo` by `tasks.fsx brand`, never written here by hand. PNG for
    /// the icon rather than SVG because iOS takes only PNG for a home-screen icon, and base64
    /// rather than a file because the process serving it has no assets directory it can
    /// count on.
    /// The manifest. `start_url` and the icon are relative to the MANIFEST's own address,
    /// which is what makes a path-mounted session install as ITSELF rather than as whatever
    /// sits at the origin root.
    ///
    /// `scope` is the exception, and deliberately the whole origin: a navigation outside the
    /// scope leaves the installed app for the browser, and this shell has one — the sign-in
    /// bounce through the Manager, which lands wherever the Manager is rather than under the
    /// session's mount. A session that owns its origin loses nothing by it (there `/` and
    /// `./` are the same place); a mounted one keeps its login flow inside the app.
    let manifest =
        sprintf
            """{"name":"Yession","short_name":"Yession","start_url":"./","scope":"/","display":"standalone","orientation":"any","background_color":"%s","theme_color":"%s","icons":[{"src":"./%s","sizes":"512x512","type":"image/png","purpose":"any"}]}"""
            ground ground (RelativeUrl.inDocument DocumentBase.manifest (SessionRoute.relative Icon))

    /// The head tags, given the routes as this document addresses them. Emitted by both
    /// shells; the Manager takes only the icon and the tint (there is nothing to install
    /// about a session list).
    let headTags (manifestUrl: string) (iconUrl: string) (faviconUrl: string) =
        String.concat "" [
            sprintf "<link rel=\"manifest\" href=\"%s\">" manifestUrl
            // Two tab marks for two kinds of browser, not a fallback beside a primary: one
            // that takes SVG prefers it and draws the cut made for 16px; Safari ignores it
            // and takes the PNG, as it always has.
            sprintf "<link rel=\"icon\" type=\"image/svg+xml\" href=\"%s\">" faviconUrl
            sprintf "<link rel=\"icon\" type=\"image/png\" href=\"%s\">" iconUrl
            sprintf "<link rel=\"apple-touch-icon\" href=\"%s\">" iconUrl
            sprintf "<meta name=\"theme-color\" content=\"%s\">" ground
            "<meta name=\"apple-mobile-web-app-capable\" content=\"yes\">"
            "<meta name=\"apple-mobile-web-app-status-bar-style\" content=\"black\">"
            "<meta name=\"apple-mobile-web-app-title\" content=\"Yession\">"
        ]

    /// The Manager's own manifest. Same shape as the shell's and the same ground, for a
    /// deployment where the Manager is the thing on the home screen — which is what it turned
    /// out to be. Root-anchored, like every address the Manager emits.
    ///
    /// This is here because of what iOS does with the colour. A site added to the home screen
    /// launches as a standalone app, and that app's WINDOW is painted in the manifest's
    /// `background_color` — which is what shows wherever no page is painted: between two
    /// documents, and beside the outgoing page during a back swipe. With no manifest there is
    /// no colour to read and the window is WHITE, so a black product flashed white on every
    /// navigation and the status bar re-tinted for a light app while it did. Photographed on
    /// an iPhone: pressing Create, and swiping back out of a session.
    ///
    /// Nothing a document says reaches that window. Not the stylesheet (the ground it paints
    /// is the page's), not `color-scheme`, not `theme-color` (the browser's bars), not the
    /// view transition in `tailwind.css` (it holds the outgoing page, and the window is
    /// behind both). The manifest is the only place the answer can be given, and it is read
    /// when the app is installed rather than on the visit — so an app added before this
    /// landed keeps the white window until it is added again.
    ///
    /// `name` says which of the two apps this is, because a phone may hold both: the Manager
    /// and a session installed from its own shell.
    let managerManifest (iconUrl: string) =
        sprintf
            """{"name":"Yession Manager","short_name":"Yession","start_url":"/","scope":"/","display":"standalone","orientation":"any","background_color":"%s","theme_color":"%s","icons":[{"src":"%s","sizes":"512x512","type":"image/png","purpose":"any"}]}"""
            ground ground iconUrl

    /// The Manager's half: the mark in the tab, the tint on the browser's bars, and the
    /// manifest above — the three statements that differ from a session's only in what they
    /// point at. The apple tags ride along for the same reason they do on the shell: they are
    /// where an iOS before 16.4 looks, and the status bar over a black app is a stated thing
    /// rather than a default.
    let managerHeadTags (manifestUrl: string) (iconUrl: string) (faviconUrl: string) =
        String.concat "" [
            sprintf "<link rel=\"manifest\" href=\"%s\">" manifestUrl
            sprintf "<link rel=\"icon\" type=\"image/svg+xml\" href=\"%s\">" faviconUrl
            sprintf "<link rel=\"icon\" type=\"image/png\" href=\"%s\">" iconUrl
            sprintf "<link rel=\"apple-touch-icon\" href=\"%s\">" iconUrl
            sprintf "<meta name=\"theme-color\" content=\"%s\">" ground
            "<meta name=\"apple-mobile-web-app-capable\" content=\"yes\">"
            "<meta name=\"apple-mobile-web-app-status-bar-style\" content=\"black\">"
            "<meta name=\"apple-mobile-web-app-title\" content=\"Yession\">"
        ]
