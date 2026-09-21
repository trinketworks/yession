module Yession.Tests.Routes

// The session's HTTP contract (`Yession.App.SessionRoute`): the paths its server matches,
// its shell emits, and its browser client fetches, all from one declaration — and the two
// request bodies that go with them. Pure — the cheapest tier covers it.

open Fable.Pyxpecto
open Yession.Domain
open Yession.App

let private offset (n: int64) =
    match EventOffset.create n with
    | Ok o -> o
    | Error e -> failwith e

/// Every route, so the properties below are checked against the whole surface rather than a
/// remembered subset. A new case fails `relative`'s and `parse`'s matches until handled;
/// this list is what makes it also fail these tests until it is listed.
let private every =
    [ Shell
      // A static file, at the depth a build actually emits: one at the top of the set and one
      // in a directory, because the route carries the whole tail and `relative` and `parse`
      // have to stay exact inverses over both.
      Asset ("K3nR7pQx2wL0", "client.js")
      Asset ("8fZs1mVb4tHc", "fonts/monaspace-neon-latin-300-normal.woff2")
      // The worker sits at the mount root rather than in the asset set, because a worker
      // controls its own path and below: under `assets/<build>/` it would control the assets
      // and nothing else.
      ServiceWorker
      Signal
      Me
      Login
      Callback
      // The event log's two halves: the cursor a client sends (both forms — from the
      // beginning, and from an offset it has folded through) and the range the server
      // redirects it to, which is the only form that carries events.
      EventsAfter None
      EventsAfter (Some (offset 0L))
      EventsAfter (Some (offset 4711L))
      Events (0L, 99L)
      Events (100L, 136L)
      // A terminal's transcript cursor and ranges (Plan 22), and the keyframes beside them
      // (Plan 14): deeper paths than anything else here, and the set the router has to keep
      // apart — `terminals/<id>`, `terminals/<id>/after/<n>`, `terminals/<id>/<a>-<b>` and
      // `terminals/<id>/keyframes/<n>` differ only by depth and by shape.
      TerminalTranscriptAfter ("term-a", None)
      TerminalTranscriptAfter ("term-a", Some 0)
      TerminalTranscriptAfter ("term-a", Some 4711)
      TerminalTranscriptRange ("term-a", 0, 499)
      TerminalTranscriptRange ("term-a", 500, 612)
      TerminalKeyframe ("term-a", 0)
      TerminalKeyframe ("term-a", 41)
      ClaudeStatus
      Claude ClaudeAction.Begin
      Claude ClaudeAction.Complete
      Claude ClaudeAction.Token
      Claude ClaudeAction.Disconnect
      // The repo picker's two reads: the listing, and one repo's branches — a path that
      // shares its head with the listing's and is told apart by depth.
      GitHubRepos
      GitHubBranches ("octo", "hello")
      GitHubPullHead ("octo", "hello", "42")
      Queries ]

let private methodOf (route: SessionRoute) =
    match route with
    | Signal
    | Claude _ -> "POST"
    | _ -> "GET"

let private routeTests =
    testList "Session route contract" [
        testCase "no route renders root-anchored inside a document" <| fun () ->
            // The property the type exists for: a session may be mounted under a path by an
            // operator's proxy, so a leading slash would send the browser to the origin root
            // — the Manager, or nothing. Inside a document that declared its base, `relative`
            // never emits one; and a `RelativeUrl` cannot be spelled any other way without
            // naming a mount (`under`), which is why no caller can write that bug.
            let inShell, _ = DocumentBase.declare ""
            for route in every do
                Expect.isFalse
                    ((RelativeUrl.inDocument inShell (SessionRoute.relative route)).StartsWith "/")
                    (sprintf "%A renders relative to the mount point" route)

        testCase "declaring a base writes the tag that makes it one" <| fun () ->
            // The tag and the witness are one value, so a document holding the proof has
            // written the tag — that is the whole reason the constructor returns both. The
            // trailing slash is what makes `/s/abc` and `/s/abc/` resolve alike.
            let _, tag = DocumentBase.declare "/s/abc"
            Expect.equal tag "<base href=\"/s/abc/\">" "the base is the mount, with a trailing slash"
            let _, root = DocumentBase.declare ""
            Expect.equal root "<base href=\"/\">" "and an origin root is the root"

        testCase "under a mount, every route is root-anchored with one slash" <| fun () ->
            // The other spelling, for a document that declared no base or a server naming
            // its own paths: `mount + "/" + relative`, written here once instead of at every
            // site that used to write it by hand — and never a double or a missing slash,
            // whatever the mount.
            for route in every do
                let rooted = RelativeUrl.under "" (SessionRoute.relative route)
                Expect.isTrue (rooted.StartsWith "/") (sprintf "%A is root-anchored at an origin root" route)
                Expect.isFalse (rooted.StartsWith "//") (sprintf "%A has one slash, not two" route)
                let mounted = RelativeUrl.under "/s/abc" (SessionRoute.relative route)
                Expect.isTrue (mounted.StartsWith "/s/abc/") (sprintf "%A sits under its mount" route)

        testCase "every route round-trips through its own rendering" <| fun () ->
            // Rendering and matching are two directions of one declaration; this is what
            // stops them drifting the way three hand-written copies of "/client.js" could.
            for route in every do
                let path = RelativeUrl.under "" (SessionRoute.relative route)
                Expect.equal
                    (SessionRoute.parse (methodOf route) path)
                    (Some route)
                    (sprintf "%A parses back from %s" route path)

        testCase "the shell is the mount point itself" <| fun () ->
            let inShell, _ = DocumentBase.declare ""
            Expect.equal (RelativeUrl.inDocument inShell (SessionRoute.relative Shell)) "" "so `<base href>` alone addresses it"
            Expect.equal (SessionRoute.parse "GET" "/") (Some Shell) "served at the mount root"

        testCase "a route reached with the wrong method is no route at all" <| fun () ->
            // None, not a 405: an unknown path and a method mismatch answer identically, as
            // they did when the server matched on (method, path) pairs directly.
            Expect.equal (SessionRoute.parse "GET" "/signal") None "signalling is POST only"
            Expect.equal (SessionRoute.parse "POST" "/me") None "the auth probe is GET only"
            Expect.equal (SessionRoute.parse "POST" "/claude") None "the status read is GET only"
            Expect.equal (SessionRoute.parse "GET" "/claude/begin") None "the panel actions are POST only"

        testCase "the cursor takes an offset, or none at all" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/events") (Some (EventsAfter None)) "no cursor reads from the beginning"
            Expect.equal (SessionRoute.parse "GET" "/events/after/12") (Some (EventsAfter (Some (offset 12L)))) "the cursor is parsed"
            Expect.equal (SessionRoute.parse "GET" "/events/after/-1") None "a negative offset is not an offset"
            Expect.equal (SessionRoute.parse "GET" "/events/after/x") None "a non-numeric cursor is rejected"
            Expect.equal (SessionRoute.parse "GET" "/events/after") None "the cursor needs its offset"

        testCase "a range must name bounds this server could actually have" <| fun () ->
            // The rule that keeps a partial answer from being kept for ever at an address
            // that promises a whole range: a range the parse will not accept is a 404, and
            // 404 is the only safe answer to an address whose bytes are not settled.
            Expect.equal (SessionRoute.parse "GET" "/events/0-99") (Some (Events (0L, 99L))) "a range is parsed"
            Expect.equal (SessionRoute.parse "GET" "/events/100-100") (Some (Events (100L, 100L))) "one event is a range"
            Expect.equal (SessionRoute.parse "GET" "/events/9-8") None "an inverted range is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/-1-4") None "a negative bound is not a route"
            Expect.equal
                (SessionRoute.parse "GET" (sprintf "/events/0-%d" EventChunk.size))
                None
                "a range longer than one answer carries is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/0-") None "a half-written range is not a route"
            Expect.equal (SessionRoute.parse "GET" "/events/x-y") None "a non-numeric range is rejected"

        testCase "an unknown path is not a route" <| fun () ->
            Expect.equal (SessionRoute.parse "GET" "/nope") None "unclaimed"
            Expect.equal (SessionRoute.parse "GET" "/claude/nope") None "an unknown action is not a route"

        testCase "a mounted session's absolute URLs join with exactly one slash" <| fun () ->
            // What a client outside a browser uses, having no document base to resolve
            // against — and where a caller could otherwise double or drop the separator.
            Expect.equal
                (SessionRoute.at "https://example.com/s/abc" (Events (300L, 336L)))
                "https://example.com/s/abc/events/300-336"
                "path-mounted"
            Expect.equal
                (SessionRoute.at "http://127.0.0.1:54321/" Signal)
                "http://127.0.0.1:54321/signal"
                "a trailing slash on the address does not double up"
            Expect.equal
                (SessionRoute.at "http://127.0.0.1:54321" Shell)
                "http://127.0.0.1:54321/"
                "the shell is the address itself"
    ]

let private mountTests =
    // A session mounted under a path. The operator's proxy forwards the public path
    // unchanged; the session strips its own prefix. The alternative contract — proxy
    // strips, session serves at root — would make correctness depend on per-proxy
    // rewriting behaviour, which cannot be verified here.
    let mount = "/s/01hx"
    testList "A path-mounted session" [
        testCase "claims its own prefix and nothing outside it" <| fun () ->
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/assets/abc123/client.js") (Some (Asset ("abc123", "client.js"))) "its bundle"
            Expect.equal (SessionRoute.parseUnder mount "POST" "/s/01hx/signal") (Some Signal) "its signalling"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/events") (Some (EventsAfter None)) "its event cursor"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/events/0-99") (Some (Events (0L, 99L))) "its event ranges"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/assets/abc123/client.js") None "the origin root is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/other/assets/abc123/client.js") None "a sibling's prefix is not its own"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hxtra/assets/abc123/client.js") None "a prefix it merely starts with is not its own"

        testCase "is reached at its mount with or without a trailing slash" <| fun () ->
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx/") (Some Shell) "the usual form"
            Expect.equal (SessionRoute.parseUnder mount "GET" "/s/01hx") (Some Shell) "and the bare mount"

        testCase "an unmounted session is unchanged" <| fun () ->
            Expect.equal (SessionRoute.parseUnder "" "GET" "/assets/abc123/client.js") (Some (Asset ("abc123", "client.js"))) "an origin-root session claims the path as written"
            Expect.equal (SessionRoute.parseUnder "" "GET" "/") (Some Shell) "including its shell"

        testCase "what the browser asks for is what the session claims" <| fun () ->
            // The round trip that matters end to end: `<base href="<mount>/">` makes the
            // browser resolve each relative route to `<mount>/<relative>`, and that is
            // exactly what `parseUnder` accepts back. If either side changed alone, this
            // would fail.
            for route in every do
                let asked = RelativeUrl.under mount (SessionRoute.relative route)
                Expect.equal
                    (SessionRoute.parseUnder mount (methodOf route) asked)
                    (Some route)
                    (sprintf "%A resolves to %s and comes back" route asked)
    ]

/// Every management route, for the same reason `every` above lists every session route: a
/// case added to `ManagerRoute` fails `path`'s and `parse`'s matches until handled, and this
/// list is what makes it fail these tests until it is listed.
let private everyManager =
    let ui = SessionId.create "ui-1" |> Result.defaultWith failwith
    [ ManagerRoute.Home
      ManagerRoute.Asset ("K3nR7pQx2wL0", "app.css")
      ManagerRoute.Asset ("8fZs1mVb4tHc", "fonts/noto-sans-latin-400-normal.woff2")
      ManagerRoute.Icon
      ManagerRoute.CreateSession
      ManagerRoute.SessionRegistry
      ManagerRoute.SessionRows
      ManagerRoute.Session (ui, SessionVerb.Launch)
      ManagerRoute.Session (ui, SessionVerb.Stop)
      ManagerRoute.Session (ui, SessionVerb.Archive)
      ManagerRoute.Session (ui, SessionVerb.Unarchive)
      ManagerRoute.OpenSession ui
      ManagerRoute.SessionReady ui
      ManagerRoute.DeclareMcpServer
      ManagerRoute.WithdrawMcpServer ]

let private managerMethodOf (route: ManagerRoute) =
    match route with
    | ManagerRoute.CreateSession
    | ManagerRoute.Session _
    | ManagerRoute.DeclareMcpServer
    | ManagerRoute.WithdrawMcpServer -> "POST"
    | _ -> "GET"

let private managerRouteTests =
    testList "Manager route contract" [
        testCase "every route is root-anchored, with one slash" <| fun () ->
            // The property the Manager's contract has and a session's must not: the Manager
            // owns its origin root (`ManagerOrigin` refuses a path), and its pages sit at
            // different depths, so only a root-anchored address is right from all of them.
            for route in everyManager do
                let path = ManagerRoute.path route
                Expect.isTrue (path.StartsWith "/") (sprintf "%A is root-anchored" route)
                Expect.isFalse (path.StartsWith "//") (sprintf "%A has one slash, not two" route)

        testCase "every route round-trips through its own rendering" <| fun () ->
            // The page emits `path`, the server matches `parse`: two directions of one
            // declaration, which is what stops the inline script's `/sessions/{id}/launch`
            // and the router's `[| id; "launch" |]` drifting apart.
            for route in everyManager do
                let path = ManagerRoute.path route
                Expect.equal
                    (ManagerRoute.parse (managerMethodOf route) path)
                    (Ok route)
                    (sprintf "%A parses back from %s" route path)

        testCase "a route reached with the wrong method is no route at all" <| fun () ->
            let unclaimed = Error ManagerMiss.Unclaimed
            Expect.equal (ManagerRoute.parse "GET" "/sessions") unclaimed "creating is POST only"
            Expect.equal (ManagerRoute.parse "POST" "/sessions/stream") unclaimed "the registry is GET only"
            Expect.equal (ManagerRoute.parse "GET" "/sessions/ui-1/launch") unclaimed "a lifecycle act is POST only"
            Expect.equal (ManagerRoute.parse "POST" "/sessions/ui-1/open") unclaimed "opening is a navigation, GET only"

        testCase "a session path with something that is not a session id is a miss that says so" <| fun () ->
            // Distinct from unclaimed: the shape is the Manager's, so the Manager answers —
            // with the id it was given and the rule it broke, which is what lets the router
            // say 400 rather than a 404 that reads as "no such session".
            let malformed (raw: string) =
                function
                | Error (ManagerMiss.MalformedSessionId (got, reason)) ->
                    Expect.equal got raw "carries the id as given"
                    Expect.isTrue (reason.Contains "SessionId") "and the rule it broke"
                | other -> failwithf "expected a malformed-id miss for %s, got %A" raw other
            ManagerRoute.parse "GET" "/sessions/-nope/open" |> malformed "-nope"
            ManagerRoute.parse "POST" "/sessions/x/launch" |> malformed "x"
            ManagerRoute.parse "GET" "/sessions//open" |> malformed ""

        testCase "a session's routes are not the Manager's" <| fun () ->
            // The two static shapes are shared — the Manager links the same stylesheet and
            // wears the same mark — and NOTHING else is: a session's `/me`, `/signal`,
            // `/events` at the Manager's origin are unclaimed and fall through.
            let unclaimed = Error ManagerMiss.Unclaimed
            Expect.equal (ManagerRoute.parse "GET" "/me") unclaimed "the auth probe"
            Expect.equal (ManagerRoute.parse "POST" "/signal") unclaimed "signalling"
            Expect.equal (ManagerRoute.parse "GET" "/events") unclaimed "the event cursor"
            Expect.equal (ManagerRoute.parse "GET" "/sw.js") unclaimed "the worker"
            Expect.equal (ManagerRoute.parse "GET" "/nope") unclaimed "and an unknown path is unclaimed"

        testCase "the static shapes are the session's own, at the origin root" <| fun () ->
            // What a session emits for a file, anchored at a root, is what the Manager claims
            // for its own copy of that file — the two servers cannot disagree about where a
            // build's files sit, because there is one rendering.
            let file = Asset ("K3nR7pQx2wL0", "app.css")
            Expect.equal
                (ManagerRoute.parse "GET" (RelativeUrl.under "" (SessionRoute.relative file)))
                (Ok (ManagerRoute.Asset ("K3nR7pQx2wL0", "app.css")))
                "the asset set"
            Expect.equal
                (ManagerRoute.parse "GET" (RelativeUrl.under "" (SessionRoute.relative Icon)))
                (Ok ManagerRoute.Icon)
                "the mark"

        testCase "a Manager's absolute URLs join with exactly one slash" <| fun () ->
            // The session client's reconnect link and the registry subscriber, given an
            // origin with or without its trailing slash.
            let ui = SessionId.create "ui-1" |> Result.defaultWith failwith
            Expect.equal
                (ManagerRoute.at "https://manager.example" (ManagerRoute.OpenSession ui))
                "https://manager.example/sessions/ui-1/open"
                "bare origin"
            Expect.equal
                (ManagerRoute.at "http://127.0.0.1:8321/" ManagerRoute.SessionRegistry)
                "http://127.0.0.1:8321/sessions/stream"
                "a trailing slash does not double up"
            Expect.equal (ManagerRoute.at "http://127.0.0.1:8321" ManagerRoute.Home) "http://127.0.0.1:8321/" "the page is the origin itself"
    ]

// --- the bodies those write routes carry ---------------------------------------------------
//
// The BYTES, and deliberately so. A connection panel is the browser at one end and the
// session at the other, and until now each end wrote the shape for itself: an anonymous
// record stringified in `app/browser/Browser.fs`, a decoder in `app/ClaudeConnection.fs`
// and `app/GitHubConnection.fs`. One declaration answers both now, and these cases pin
// what it puts on the wire so that "one declaration" cannot quietly become "a different
// wire" — the field names, their order, and the fact that an absent one is not there at
// all rather than there and blank.

let private claudeBodyTests =
    testList "a Claude panel write" [

        testCase "says the scope alone when it has nothing else to say" <| fun () ->
            Expect.equal
                (Codec.toString ClaudeRequest.codec (ClaudeRequest.scoped "session"))
                """{"scope":"session"}"""
                "no key for a code or a token nobody pasted"

        testCase "says a pasted code beside the scope" <| fun () ->
            let request : ClaudeRequest = { Scope = "mine"; Code = Some "abc123"; Token = None }
            Expect.equal
                (Codec.toString ClaudeRequest.codec request)
                """{"scope":"mine","code":"abc123"}"""
                "the code, under the name the session reads it by"

        testCase "says a pasted token beside the scope" <| fun () ->
            let request : ClaudeRequest = { Scope = "mine"; Code = None; Token = Some "sk-ant-oat01-x" }
            Expect.equal
                (Codec.toString ClaudeRequest.codec request)
                """{"scope":"mine","token":"sk-ant-oat01-x"}"""
                "the token, under the name the session reads it by"

        testCase "is read back as the write that was sent" <| fun () ->
            let request : ClaudeRequest = { Scope = "session"; Code = Some "abc123"; Token = None }
            Expect.equal
                (Codec.fromString ClaudeRequest.codec (Codec.toString ClaudeRequest.codec request))
                (Ok request)
                "the same write"

        // The panel does not always name a scope — a GET of the status route names none at
        // all — and "mine" is what that has always meant.
        testCase "naming no scope means the signing actor's own" <| fun () ->
            Expect.equal
                (Codec.fromString ClaudeRequest.codec "{}")
                (Ok (ClaudeRequest.scoped "mine"))
                "mine, and nothing pasted"
    ]

let private githubBodyTests =
    testList "a GitHub panel write" [

        testCase "says the scope alone when it has nothing else to say" <| fun () ->
            Expect.equal
                (Codec.toString GitHubRequest.codec (GitHubRequest.scoped "mine"))
                """{"scope":"mine"}"""
                "no key for a token nobody pasted"

        testCase "says a pasted token beside the scope" <| fun () ->
            let request : GitHubRequest = { Scope = "session"; Token = Some "ghp_abc" }
            Expect.equal
                (Codec.toString GitHubRequest.codec request)
                """{"scope":"session","token":"ghp_abc"}"""
                "the token, under the name the session reads it by"

        testCase "is read back as the write that was sent" <| fun () ->
            let request : GitHubRequest = { Scope = "session"; Token = Some "ghp_abc" }
            Expect.equal
                (Codec.fromString GitHubRequest.codec (Codec.toString GitHubRequest.codec request))
                (Ok request)
                "the same write"

        testCase "naming no scope means the signing actor's own" <| fun () ->
            Expect.equal
                (Codec.fromString GitHubRequest.codec "{}")
                (Ok (GitHubRequest.scoped "mine"))
                "mine, and nothing pasted"
    ]

let tests =
    testList "Routes" [ routeTests; mountTests; managerRouteTests; claudeBodyTests; githubBodyTests ]
