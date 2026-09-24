module Yession.Tests.ArtifactHttp

// The session's content surface over HTTP: `content/artifacts/<name>` resolves to whatever is
// latest, `content/artifacts/<name>/<version>` is those bytes for good, and everything else is
// a 404 — including a path that is a real address this build cannot serve yet.
//
// Deliberately the shape of `TranscriptHttp.fs` and `EventsHttp.fs`, because it is the same
// contract: a moving address that is never cached, redirecting to a fixed one whose bytes
// cannot change under a client. What is different, and is what these pin, is that the fixed
// address is a FILE — so the cases that matter are the door, the containment, and what a
// browser is told about bytes this build did not write.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Content
open Yession.App
open Yession.Host
open Yession.SessionProcess
open Yession.Tests.Support

/// `who=<name>` is an identity; anything else is nobody — the same stand-in the query stream's
/// route tests use, and for the same reason: the OIDC round trip is `Oidc.fs`'s subject, and
/// what is under test here is what the ROUTE does with an identity.
let private stubAuth () : SessionAuth.Auth =
    { Configure = fun _ _ _ _ -> async { return Ok () }
      IsAuthenticated = fun req -> (Interop.headerOf req "cookie").IsSome
      IdentityOf =
        fun req ->
            match Interop.headerOf req "cookie" with
            | Some cookie when cookie.StartsWith "who=" ->
                Some
                    ({ Subject = cookie.Substring 4
                       DisplayName = None
                       Attribution = AttributedUser (UserId.create (cookie.Substring 4) |> expect) } : CookieIdentity)
            | _ -> None
      BeginLogin = fun _ -> async { return None }
      HandleCallback = fun _ -> async { return Error (500, "not under test") }
      CookieName = "who" }

let private ada = [ "cookie", "who=ada" ]

let private stamp = ArtifactStamp.create "7f2a91" |> expect

let private version (name: string) (seq: int) = ArtifactRef.create name seq stamp |> expect

/// An artifacts directory holding the versions named, served the way the Session Process serves
/// it: `Signalling.start` with the store's own routes as its extra routes. So the fall-through
/// that sends a `Content` route there is under test beside the answers — a route the composition
/// forgot to compose would 404 here exactly as it would in production.
let private serving (files: (ArtifactRef * string) list) =
    async {
        let dir = TestFiles.tempDir "artifacts-http"
        for (ref, body) in files do
            TestFiles.ensureDir (sprintf "%s/%s" dir (ArtifactRef.name ref))
            TestFiles.write (Artifacts.pathOf dir ref) body
        let! server, _ =
            Signalling.start
                (SessionId.create "artifact-http" |> expect)
                ignore
                None
                None
                None
                (Some (Artifacts.routes (stubAuth ()) dir ""))
                (fun _ -> "no peer token is minted here")
                ""
                None
                false
                0
        let origin = sprintf "http://127.0.0.1:%d" (Interop.serverPort server)
        let at (ref: ContentRef) = SessionRoute.at origin (SessionRoute.Content ref)
        return dir, origin, at, (fun () -> async { server.close ignore })
    }

let private routeTests =
    testList "the content surface" [

        testCaseAsync "a name answers with the address of its latest version, never with bytes" <|
            async {
                // The cursor-and-range shape, for a file: what moves is a redirect nobody may
                // keep, and what a client keeps is an address whose bytes cannot change. An
                // `<img src>` follows this without knowing versions exist.
                let! _, _, at, stop =
                    serving [ version "chart.png" 0, "first"; version "chart.png" 1, "second" ]
                let! reply = TestHttp.getUnredirected ada (at (ArtifactRef.directory (version "chart.png" 0)))
                Expect.equal reply.Status 307 "the name redirects"
                Expect.stringContains
                    (TestHttp.requiredHeader "location" reply)
                    "content/artifacts/chart.png/0001-7f2a91"
                    "to the newest version, not the first"
                Expect.equal (TestHttp.requiredHeader "cache-control" reply) "no-store" "what resolves is never kept"
                do! stop ()
            }

        testCaseAsync "a version answers its bytes, and says they may be kept for good" <|
            async {
                let! _, _, at, stop = serving [ version "chart.png" 0, "PNG-ish bytes" ]
                let! reply = TestHttp.getNoStore ada (at (ArtifactRef.content (version "chart.png" 0)))
                Expect.equal reply.Status 200 "the version serves"
                Expect.equal reply.Body "PNG-ish bytes" "its own bytes"
                Expect.equal (TestHttp.requiredHeader "content-type" reply) "image/png" "the type the NAME implies"
                Expect.equal
                    (TestHttp.requiredHeader "cache-control" reply)
                    CachePolicy.contentVersion
                    "immutable, because this address can never hold other bytes"
                Expect.stringContains
                    (TestHttp.requiredHeader "content-disposition" reply)
                    "inline"
                    "an image is shown rather than downloaded"
                do! stop ()
            }

        testCaseAsync "bytes this session did not write are served inert" <|
            async {
                // An artifact is a file somebody else chose the contents of, on the session's
                // OWN origin — where every cookie in this session lives. An SVG is markup, and
                // markup navigated to directly would run as this origin without these two.
                let! _, _, at, stop = serving [ version "diagram.svg" 0, "<svg/>" ]
                let! reply = TestHttp.getNoStore ada (at (ArtifactRef.content (version "diagram.svg" 0)))
                Expect.equal (TestHttp.requiredHeader "x-content-type-options" reply) "nosniff" "the recorded type, never a sniffed one"
                Expect.stringContains
                    (TestHttp.requiredHeader "content-security-policy" reply)
                    "sandbox"
                    "and nothing in it may act"
                do! stop ()
            }

        testCaseAsync "a type this build cannot draw is a download, never a guess" <|
            async {
                let! _, _, at, stop = serving [ version "dump.bin" 0, "not anything we claim to know" ]
                let! reply = TestHttp.getNoStore ada (at (ArtifactRef.content (version "dump.bin" 0)))
                Expect.equal (TestHttp.requiredHeader "content-type" reply) "application/octet-stream" "unclaimed is unclaimed"
                Expect.stringContains (TestHttp.requiredHeader "content-disposition" reply) "attachment" "so the browser saves it"
                do! stop ()
            }

        testCaseAsync "no identity, no content" <|
            async {
                // An artifact is session state: what is in it, and even which names exist, is
                // for the people in the session.
                let! _, _, at, stop = serving [ version "secret.png" 0, "the bytes" ]
                let! reply = TestHttp.getNoStore [] (at (ArtifactRef.content (version "secret.png" 0)))
                Expect.equal reply.Status 401 "the surface is gated"
                Expect.isFalse (reply.Body.Contains "the bytes") "and it leaked nothing on the way out"
                let! named = TestHttp.getUnredirected [] (at (ArtifactRef.directory (version "secret.png" 0)))
                Expect.equal named.Status 401 "including the address that would resolve one"
                do! stop ()
            }

        testCaseAsync "an address the store does not hold is a 404, whichever form it takes" <|
            async {
                let! _, _, at, stop = serving [ version "chart.png" 0, "bytes" ]
                let! unknownName = TestHttp.getUnredirected ada (at (ArtifactRef.directory (version "nobody.png" 0)))
                Expect.equal unknownName.Status 404 "a name nobody shared"
                let! unknownVersion = TestHttp.getNoStore ada (at (ArtifactRef.content (version "chart.png" 7)))
                Expect.equal unknownVersion.Status 404 "and a version that was never minted"
                do! stop ()
            }

        testCaseAsync "a directory of the content root this build cannot serve yet is a 404, not an error" <|
            async {
                // `repos/…` is a real address in this space — it is what the pane will show
                // next — and until something serves it, it is simply absent.
                let! _, _, at, stop = serving []
                let! reply =
                    TestHttp.getNoStore ada (at (ContentRef.create "repos/octo/hello/README.md" |> expect))
                Expect.equal reply.Status 404 "absent, like any other address with nothing behind it"
                do! stop ()
            }

        testCaseAsync "a path that would climb out of the content root never reaches a file" <|
            async {
                // Percent-encoded, because an unencoded `..` is folded by the client before it
                // is ever sent — and it is the ENCODED form that a route which normalised late
                // would let through. `ContentRef.create` is the parse, so this dies at the
                // router with no file path ever built.
                let! _, origin, _, stop = serving []
                for climb in [ "%2e%2e%2f%2e%2e%2fetc%2fpasswd"; "..%2f..%2fetc%2fpasswd"; ".ssh%2fid_rsa" ] do
                    let! reply = TestHttp.getNoStore ada (sprintf "%s/content/artifacts/%s" origin climb)
                    Expect.equal reply.Status 404 (sprintf "'%s' addresses nothing" climb)
                    Expect.isFalse (reply.Body.Contains "root:") "and answered with no file"
                do! stop ()
            }

        testCaseAsync "a leaf that is a symlink out of the store serves nothing" <|
            async {
                // The downstream guard, against the fault the URL parse cannot see: the store
                // is bind-mounted into every sandbox, so a sandbox can put a symlink in it, and
                // the path the store builds for that leaf is perfectly well-formed.
                let! dir, _, at, stop = serving [ version "escape.png" 1, "a real version, so the name exists" ]
                let leak = version "escape.png" 0
                TestFiles.symlink "/etc/hostname" (Artifacts.pathOf dir leak)
                let! reply = TestHttp.getNoStore ada (at (ArtifactRef.content leak))
                Expect.equal reply.Status 404 "a leaf pointing out of the store is not content"
                do! stop ()
            }
    ]

let tests = testList "ArtifactHttp" [ routeTests ]
