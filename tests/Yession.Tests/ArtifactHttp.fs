module Yession.Tests.ArtifactHttp

// The session's content surface over HTTP: `content/artifacts/<name>` resolves to whatever is
// latest, `content/artifacts/<name>/<version>` is those bytes for good, and everything else is
// a 404 — including a path that is a real address this build cannot serve yet.
//
// And the same store read by something that is not a browser at all (`Artifacts.read`), which
// is how an agent looks at what it shared: same addresses, same containment, a different
// destination — so the two readers are pinned against one resolution rather than two.
//
// Deliberately the shape of `TranscriptHttp.fs` and `EventsHttp.fs`, because it is the same
// contract: a moving address that is never cached, redirecting to a fixed one whose bytes
// cannot change under a client. What is different, and is what these pin, is that the fixed
// address is a FILE — so the cases that matter are the door, the containment, and what a
// browser is told about bytes this build did not write.

open Fable.NodeExtras
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Content
open Yession.App
open Yession.Host
open Yession.Session
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

/// An artifacts directory holding the versions named, served the way the Session serves
/// it: `Signalling.start` with the store's own routes as its extra routes. So the fall-through
/// that sends a `Content` route there is under test beside the answers — a route the composition
/// forgot to compose would 404 here exactly as it would in production.
let private serving (files: (ArtifactRef * string) list) =
    async {
        let log = Yession.Session.InMemoryEventLog.create (SessionId.create "artifact-http" |> expect) (fun () -> System.DateTimeOffset.UtcNow)
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
                (Some (Artifacts.routes (stubAuth ()) (Artifacts.receive log dir) (Artifacts.stage (TestFiles.tempDir "artifacts-http-staged")) dir ""))
                (fun _ -> "no peer token is minted here")
                ""
                None
                false
                0
        let origin = sprintf "http://127.0.0.1:%d" (serverPort server)
        let at (ref: ContentRef) = SessionRoute.at origin (SessionRoute.Content ref)
        return dir, origin, at, (fun () -> async { server.close ignore })
    }

/// An empty store served for writing, with the log the writes are recorded on and the URL a
/// `PUT` of a name goes to.
let private receiving () =
    async {
        let log = Yession.Session.InMemoryEventLog.create (SessionId.create "artifact-upload" |> expect) (fun () -> System.DateTimeOffset.UtcNow)
        let dir = TestFiles.tempDir "artifacts-upload"
        let! server, _ =
            Signalling.start
                (SessionId.create "artifact-upload" |> expect)
                ignore
                None
                None
                None
                (Some (Artifacts.routes (stubAuth ()) (Artifacts.receive log dir) (Artifacts.stage (TestFiles.tempDir "artifacts-upload-staged")) dir ""))
                (fun _ -> "no peer token is minted here")
                ""
                None
                false
                0
        let origin = sprintf "http://127.0.0.1:%d" (serverPort server)
        let named (name: string) =
            SessionRoute.at origin (SessionRoute.ContentUpload (ArtifactRef.directoryOf name |> expect))
        let shared () =
            async {
                let! page = log.Read None 1000
                return
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | SessionEvent.ArtifactShared fact -> Some fact
                        | _ -> None)
            }
        return dir, origin, named, shared, (fun () -> async { server.close ignore })
    }

let private uploadTests =
    testList "sending a file in" [

        testCaseAsync "a name put is that name's next version, recorded as the sender's" <|
            async {
                let! _, origin, named, shared, stop = receiving ()
                let! first = TestHttp.put ada "the first" (named "notes.md")
                Expect.equal first.Status 201 "the store took it"
                Expect.isTrue (first.Body.StartsWith "file:///artifacts/notes.md/0000-") "answered with the version it minted"
                let! second = TestHttp.put ada "the second" (named "notes.md")
                Expect.equal second.Status 201 "and a second time, as another version"
                let! facts = shared ()
                Expect.equal (facts |> List.map (fun f -> ArtifactRef.seq f.Ref)) [ 0; 1 ] "two versions, never one overwritten"
                Expect.equal
                    (facts |> List.map (fun f -> f.Actor))
                    [ UserRef (UserId.create "ada" |> expect); UserRef (UserId.create "ada" |> expect) ]
                    "each the cookie's user's"
                // Read back through the surface it was put to: what a browser put is where it
                // then reads it.
                let! latest = TestHttp.getNoStore ada (sprintf "%s/content/artifacts/notes.md" origin)
                Expect.equal latest.Body "the second" "the name now resolves to what was sent last"
                do! stop ()
            }

        testCaseAsync "no identity, no upload" <|
            async {
                let! _, _, named, shared, stop = receiving ()
                let! reply = TestHttp.put [] "anything" (named "notes.md")
                Expect.equal reply.Status 401 "the write is gated like the read"
                let! facts = shared ()
                Expect.isEmpty facts "and nothing was recorded"
                do! stop ()
            }

        testCaseAsync "a version is never written to" <|
            async {
                let! _, origin, _, shared, stop = receiving ()
                let! reply = TestHttp.put ada "other bytes" (sprintf "%s/content/artifacts/notes.md/0000-7f2a91" origin)
                Expect.equal reply.Status 409 "an artifact is immutable; the store picks the next version"
                let! facts = shared ()
                Expect.isEmpty facts "and nothing was recorded"
                do! stop ()
            }

        testCaseAsync "a body said to be past the cap is refused before a byte is kept" <|
            async {
                // The store itself, handed a body whose declared length is too big: a fetch
                // will not send a length its body disagrees with, and the point is that the
                // declaration alone is enough to refuse on.
                let log = Yession.Session.InMemoryEventLog.create (SessionId.create "artifact-cap" |> expect) (fun () -> System.DateTimeOffset.UtcNow)
                let dir = TestFiles.tempDir "artifacts-cap"
                let source = TestFiles.tempDir "artifacts-cap-source" + "/body"
                TestFiles.write source "short"
                let! outcome =
                    Artifacts.receive log dir ActorRef.Agent "huge.bin" (Some (ArtifactRef.cap + 1L)) (openFileStream source)
                match outcome with
                | Error (Artifacts.UploadRefusal.Refused said) -> Expect.stringContains said "at most 100 MB" "the refusal quotes the cap"
                | other -> failwithf "expected a refusal, got %A" other
                Expect.isEmpty (Artifacts.listing dir) "and no version on disk"
            }
    ]

/// A staging directory and an artifacts directory, served for attachment uploads, with the
/// store the drain and the sweep would be handed.
let private staging () =
    async {
        let log = Yession.Session.InMemoryEventLog.create (SessionId.create "artifact-staging" |> expect) (fun () -> System.DateTimeOffset.UtcNow)
        let dir = TestFiles.tempDir "artifacts-staging"
        let stagedDir = TestFiles.tempDir "artifacts-staged"
        let! server, _ =
            Signalling.start
                (SessionId.create "artifact-staging" |> expect)
                ignore
                None
                None
                None
                (Some (Artifacts.routes (stubAuth ()) (Artifacts.receive log dir) (Artifacts.stage stagedDir) dir ""))
                (fun _ -> "no peer token is minted here")
                ""
                None
                false
                0
        let origin = sprintf "http://127.0.0.1:%d" (serverPort server)
        let upload (id: string) = SessionRoute.at origin (SessionRoute.StagedUpload (UploadId.create id |> expect))
        let recorded () =
            async {
                let! page = log.Read None 1000
                return page.Events
            }
        return dir, Artifacts.staging log dir stagedDir, upload, recorded, (fun () -> async { server.close ignore })
    }

let private stagingTests =
    testList "attaching a file to a message" [

        testCaseAsync "an attachment's bytes wait where nobody can see them, and a retry is the same upload" <|
            async {
                let! dir, store, upload, recorded, stop = staging ()
                let id = UploadId.create "u-1" |> expect
                let! first = TestHttp.put ada "pixels" (upload "u-1")
                Expect.equal first.Status 204 "staged"
                Expect.isTrue (store.Arrived id) "and arrived whole"
                let! again = TestHttp.put ada "pixels" (upload "u-1")
                Expect.equal again.Status 204 "a retry of an upload that landed is answered, not refused"
                Expect.equal (store.Listed () |> List.map (fst >> UploadId.value)) [ "u-1" ] "one file, not two"
                Expect.isEmpty (Artifacts.listing dir) "not an artifact yet"
                let! events = recorded ()
                Expect.isEmpty events "and nothing on the log: nobody else has seen it"
                do! stop ()
            }

        testCaseAsync "no identity, no attachment" <|
            async {
                let! _, store, upload, _, stop = staging ()
                let! reply = TestHttp.put [] "pixels" (upload "u-2")
                Expect.equal reply.Status 401 "gated like every other write"
                Expect.isFalse (store.Arrived (UploadId.create "u-2" |> expect)) "and nothing kept"
                do! stop ()
            }

        testCaseAsync "sharing a staged file makes it the next version, and leaves the staged copy for the sweep" <|
            async {
                let! dir, store, upload, recorded, stop = staging ()
                let id = UploadId.create "u-3" |> expect
                let! _ = TestHttp.put ada "pixels" (upload "u-3")
                match! store.Share ActorRef.Agent "shot.png" id with
                | Ok fact ->
                    Expect.equal (ArtifactRef.name fact.Ref) "shot.png" "under the name it was attached as"
                    Expect.equal fact.Bytes 6L "all of it"
                | Error why -> failwithf "expected a share, got %s" why
                let! events = recorded ()
                Expect.equal (List.length events) 1 "recorded once, as an artifact everybody can now see"
                Expect.equal (Artifacts.listing dir |> List.length) 1 "in the store"
                Expect.isTrue (store.Arrived id) "the staged copy stays, so a drain that dies can share it again"
                store.Discard id
                Expect.isFalse (store.Arrived id) "until it is discarded"
                match! store.Share ActorRef.Agent "shot.png" id with
                | Error _ -> ()
                | Ok _ -> failwith "a discarded upload has nothing to share"
                do! stop ()
            }
    ]

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

/// An artifacts directory holding the versions named, with no server in front of it: what
/// `Artifacts.read` is asked, which is the store and nothing else.
let private holding (files: (ArtifactRef * string) list) =
    let dir = TestFiles.tempDir "artifacts-read"
    for (ref, body) in files do
        TestFiles.ensureDir (sprintf "%s/%s" dir (ArtifactRef.name ref))
        TestFiles.write (Artifacts.pathOf dir ref) body
    dir, (fun (raw: string) -> Artifacts.read dir (ContentRef.create raw |> expect))

/// A one-pixel PNG, so a picture under test is a real one: `ToolImage` reads the bytes and
/// refuses a file that is named like an image and is not one.
let private onePixelPng =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="

let private readTests =
    testList "reading the store" [

        testCaseAsync "a bare name reads whatever is latest under it" <|
            async {
                let _, read = holding [ version "notes.md" 0, "the first"; version "notes.md" 1, "the second" ]
                match! read "file:///artifacts/notes.md" with
                | Ok (FileContent.Text text) -> Expect.stringContains text "the second" "the newest version, without the caller listing them"
                | other -> failwithf "expected the latest text, got %A" other
            }

        testCaseAsync "a version reads those bytes however many came after it" <|
            async {
                let _, read = holding [ version "notes.md" 0, "the first"; version "notes.md" 1, "the second" ]
                match! read "file:///artifacts/notes.md/0000-7f2a91" with
                | Ok (FileContent.Text text) -> Expect.stringContains text "the first" "a pinned address cannot move"
                | other -> failwithf "expected the pinned text, got %A" other
            }

        // The point of the whole verb: an agent that shared a screenshot can look at it.
        testCaseAsync "a picture reads back as the picture" <|
            async {
                let dir, read = holding []
                let ref = version "shot.png" 0
                TestFiles.ensureDir (sprintf "%s/%s" dir (ArtifactRef.name ref))
                TestFiles.writeBase64 (Artifacts.pathOf dir ref) onePixelPng
                match! read "file:///artifacts/shot.png" with
                | Ok (FileContent.Image image) -> Expect.equal image.Type "image/png" "typed by its bytes, as a model is shown it"
                | other -> failwithf "expected a picture, got %A" other
            }

        testCaseAsync "a name nothing is shared under says how to find what is" <|
            async {
                let _, read = holding [ version "notes.md" 0, "the first" ]
                match! read "file:///artifacts/absent.md" with
                | Error said -> Expect.stringContains said "artifacts query" "the refusal names the listing, because an agent reads it"
                | other -> failwithf "expected a refusal, got %A" other
            }

        testCaseAsync "a leaf that is a symlink out of the store reads nothing" <|
            async {
                let dir, read = holding [ version "notes.md" 0, "the first" ]
                let outside = TestFiles.tempDir "artifacts-outside"
                TestFiles.write (outside + "/secret") "not yours"
                let ref = version "escape.md" 0
                TestFiles.ensureDir (sprintf "%s/%s" dir (ArtifactRef.name ref))
                TestFiles.symlink (outside + "/secret") (Artifacts.pathOf dir ref)
                match! read "file:///artifacts/escape.md/0000-7f2a91" with
                | Error _ -> ()
                | other -> failwithf "expected nothing to be read, got %A" other
            }
    ]

let tests = testList "ArtifactHttp" [ routeTests; uploadTests; stagingTests; readTests ]
