module Yession.Tests.Assets

// The static asset service serves what the build DECLARES (`AssetFile`) — not what happens to
// be lying in a directory, and not whatever a request path asks for.
//
// The declaration is shared with `tasks.fsx`, which matches over it exhaustively to produce
// each file, so build-and-server agreement is a compile error rather than a test. What is left
// for a test is the serving contract: the right bytes, under this build's address only, wearing
// the content type the declaration gives them.
//
// Node only: `Assets` reads files and writes an HTTP response, which is the runtime the product
// runs on. No capability — writing a temp directory is what every store suite here already does.

open FSharp
open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.App
open Yession.Host

/// What a response was. `ServerResponse` is an interface over Node's, so the cheapest honest
/// double is an object carrying the three members `Assets.serve` uses.
type private Reply =
    { mutable Status: int
      mutable Headers: obj
      mutable Body: string }

/// What `String(x)` does: a Buffer or a string, said as a string. `end` takes whatever the
/// caller had — the service passes bytes for a file and a sentence for a miss — so the double
/// records it the way Node's own writable would read it.
[<Emit("String($0)")>]
let private asString (value: obj) : string = jsNative

/// The double itself: three members, each a real F# function, so what a call to it DOES is
/// F# the compiler reads rather than statements inside a string. `Func` rather than a curried
/// lambda because Node's members take their arguments at once, which is what `serve` emits.
let private responseInto (reply: Reply) : Interop.ServerResponse =
    unbox (
        createObj
            [ "writeHead"
              ==> System.Func<int, obj, obj>(fun status headers ->
                  reply.Status <- status
                  reply.Headers <- headers
                  null)
              "write" ==> System.Func<string, bool>(fun _ -> true)
              "end"
              ==> System.Func<obj, unit>(fun body -> reply.Body <- if isNull body then "" else asString body) ])

[<Emit("($0 ?? {})[$1] ?? ''")>]
let private headerOf (headers: obj) (name: string) : string = jsNative

let private serveInto (assets: Assets.AssetSet) (build: string) (path: string) =
    let reply = { Status = 0; Headers = null; Body = "" }
    Assets.serve assets build path (responseInto reply)
    reply

let private buildOf (assets: Assets.AssetSet) = let (AssetBuild digest) = assets.Build in digest

/// A directory holding two declared files — and one that is not declared, because a stray file
/// beside the set must not become a public URL.
let private withAssets (name: string) (body: Assets.AssetSet -> 'a) : 'a =
    let dir = "tests/Yession.Tests/out/.assets/" + name
    TestFiles.removeTree dir
    TestFiles.ensureDir (dir + "/fonts")
    TestFiles.write (dir + "/" + AssetFile.path AssetFile.``app``) "body{color:red}"
    TestFiles.write (dir + "/" + AssetFile.path AssetFile.``source-serif-350``) "not really a face, but bytes are bytes"
    TestFiles.write (dir + "/undeclared.txt") "no route to here"
    body (Assets.load dir)

let tests =
    testList "Static assets" [
        testCase "a file is served as what the declaration says it is" <| fun () ->
            // Measured, not assumed: serving the set as `application/octet-stream` leaves a
            // standards-mode document with zero stylesheet rules applied and a `type="module"`
            // script refused outright — the page paints nothing and the client never starts.
            // The type is part of shipping the bytes, which is why it is declared beside them.
            withAssets "types" (fun assets ->
                let css = serveInto assets (buildOf assets) (AssetFile.path AssetFile.``app``)
                Expect.equal css.Status 200 "the stylesheet serves"
                Expect.equal (headerOf css.Headers "content-type") "text/css; charset=utf-8" "as a stylesheet"

                let face = serveInto assets (buildOf assets) (AssetFile.path AssetFile.``source-serif-350``)
                Expect.equal face.Status 200 "the face serves"
                Expect.equal (headerOf face.Headers "content-type") "font/woff2" "as a font")

        testCase "a file the build did not declare has no address" <| fun () ->
            // The service answers from the DECLARATION, so a stray file in the directory is not
            // a public URL — and nothing derived from a request path ever reaches the disk.
            withAssets "undeclared" (fun assets ->
                let reply = serveInto assets (buildOf assets) "undeclared.txt"
                Expect.equal reply.Status 404 "not declared, not served")

        testCase "an address is only ever this build's" <| fun () ->
            // A document from an older build names an older set. Answering it with CURRENT
            // bytes would write them into an `immutable` cache entry under that address —
            // wrong for a year, and unfixable from the server.
            withAssets "stale" (fun assets ->
                let reply = serveInto assets "notthisbuild" (AssetFile.path AssetFile.``app``)
                Expect.equal reply.Status 404 "another build's address is refused, not answered")

        testCase "changing any file changes every address in the set" <| fun () ->
            // Why one digest over the whole set is safe to cache forever, and why a stylesheet
            // may reference a face by a plain relative name: the sheet and the face move
            // together or not at all.
            let before = withAssets "digest" id
            let dir = "tests/Yession.Tests/out/.assets/digest"
            TestFiles.write (dir + "/" + AssetFile.path AssetFile.``source-serif-350``) "different bytes entirely"
            let after = Assets.load dir
            Expect.notEqual after.Build before.Build "a byte anywhere is a new set"
            Expect.equal (Assets.load dir).Build after.Build "and the same set always addresses the same"

        testCase "a declared file no root has is an absence, not a failure" <| fun () ->
            // Each root is TRIED in turn, and a file that is NOT THERE is that root's turn
            // passing to the next rather than an error to propagate — which is why the
            // un-built and the half-built directory both boot rather than taking the process
            // down. Only absence reads that way; see the case below for what does not.
            let dir = "tests/Yession.Tests/out/.assets/partial"
            TestFiles.removeTree dir
            TestFiles.ensureDir dir
            TestFiles.write (dir + "/" + AssetFile.path AssetFile.``app``) "body{color:red}"
            let assets = Assets.load dir
            Expect.equal
                (assets.Files |> Map.toList |> List.map fst)
                [ AssetFile.path AssetFile.``app`` ]
                "the file that is there, and only it"

        testCase "a declared file that is there and unreadable refuses the boot" <| fun () ->
            // The other half of the sentence above. `AssetFile.all` is DECLARED, so a file
            // that exists and cannot be read is a broken deployment — a permission, a mount
            // that went away, a directory where a file belongs — and no later root answers
            // for it, because nothing about it is about absence. Reading it as absence boots
            // a Manager that serves a blank shell and tells whoever looks to run `build`,
            // which will not help. So the boot stops, naming the file and what the OS said.
            //
            // Induced as a DIRECTORY where a declared file belongs (EISDIR): this runs as
            // root often enough that `chmod 000` is not a denial, and a directory is a
            // genuine there-and-unreadable for any user.
            let dir = "tests/Yession.Tests/out/.assets/unreadable"
            TestFiles.removeTree dir
            TestFiles.ensureDir (dir + "/fonts")
            TestFiles.write (dir + "/" + AssetFile.path AssetFile.``app``) "body{color:red}"
            TestFiles.ensureDir (dir + "/" + AssetFile.path AssetFile.``client``)
            Expect.throwsC
                (fun () -> Assets.load dir |> ignore)
                (fun error ->
                    Expect.stringContains error.Message (AssetFile.path AssetFile.``client``) "it names the file"
                    Expect.stringContains error.Message "EISDIR" "and what the OS said about it")

        testCase "the digest is over the paths as well as the bytes" <| fun () ->
            // The same bytes under a different NAME are a different set. A digest over the
            // bytes alone would hand a renamed file the old address, and every cached copy
            // under it would stay `immutable` and wrong — which is the one failure the
            // set-wide address exists to make impossible.
            let setOf (name: string) (file: AssetFile) =
                let dir = "tests/Yession.Tests/out/.assets/" + name
                TestFiles.removeTree dir
                TestFiles.ensureDir (dir + "/fonts")
                TestFiles.write (dir + "/" + AssetFile.path file) "the very same bytes"
                Assets.load dir

            Expect.notEqual
                (setOf "named-face" AssetFile.``source-serif-350``).Build
                (setOf "named-css" AssetFile.``app``).Build
                "one file's bytes under another file's name is another set"

        testCase "an unbuilt directory is an empty set that says so" <| fun () ->
            // The developer case: not a crash at boot, and not a bare 404 on an address that
            // looks perfectly reasonable.
            let assets = Assets.load "tests/Yession.Tests/out/.assets/never-built"
            let reply = serveInto assets (buildOf assets) (AssetFile.path AssetFile.``app``)
            Expect.equal reply.Status 404 "nothing to serve"
            Expect.stringContains reply.Body "not built" "and it names the reason"

        testCase "a document names a file through the build's address" <| fun () ->
            withAssets "urls" (fun assets ->
                let inShell, _ = DocumentBase.declare ""
                let url = RelativeUrl.inDocument inShell (AssetBuild.url assets.Build AssetFile.``app``)
                Expect.isFalse (url.StartsWith "/") "relative, so a path-mounted session resolves it under its own prefix"
                Expect.equal
                    (SessionRoute.parse "GET" (RelativeUrl.under "" (AssetBuild.url assets.Build AssetFile.``app``)))
                    (Some (Asset (buildOf assets, AssetFile.path AssetFile.``app``)))
                    "and what the document names is what the router claims")

        testCase "the set holds every declared file, each with its own path" <| fun () ->
            // `all` is hand-listed — F# will not enumerate a union's cases without reflection,
            // and `AssetFile` compiles to the browser too. So the count is checked HERE, where
            // reflection is affordable: a case added to the type and forgotten in the list is
            // never built and never served, and this is what says so.
            Expect.equal
                (List.length AssetFile.all)
                (Reflection.FSharpType.GetUnionCases(typeof<AssetFile>).Length)
                "every case of the type is in the list"
            let paths = AssetFile.all |> List.map AssetFile.path
            Expect.equal (List.distinct paths) paths "no two cases name the same file"
            Expect.equal
                (paths |> List.filter (fun path -> (AssetFile.ofPath path).IsNone))
                []
                "and every declared path resolves back to its file"
    ]
