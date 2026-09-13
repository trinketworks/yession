module Yession.Tests.NodeExtras

// `src/Fable.NodeExtras` — the Node platform slice `Fable.Node` does not type — exercised
// against the real platform.
//
// A binding project that nothing calls is only TYPE-CHECKED, and type-checking an interop
// declaration proves almost nothing: an `[<Emit>]` string, an `[<Import>]` name and an
// anonymous record's field spelling are all opaque to the compiler and all wrong in exactly
// the way that compiles. So every binding declared there is called here at least once, on
// Node, in the cheapest tier that can host it.
//
// What each case pins is the binding's PROMISE rather than the platform's behaviour: that
// `base64url` reaches Buffer as an encoding, that a decoder held between chunks really holds
// the tail of a character, that `additionalData` arrives where the algorithm reads it. Node
// is not under test; the two lines of ours that stand between F# and Node are.

open Fable.Core
open Fable.Pyxpecto
open Fable.NodeExtras
open Node.Api
open Node.Buffer

let private utf8 = BufferEncoding.Utf8

/// Poll for a condition rather than sleeping a fixed amount: what is being waited on is a
/// child process exiting, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 20
                return! loop (remaining - 20)
        }

    loop 5000

/// A fresh non-extractable AES-GCM key, imported exactly the way `app/SecretsCipher.fs` does:
/// random bytes, through base64url, back to bytes.
let private importedKey () : Async<CryptoKey> =
    let kek = crypto.randomBytes(32).toString base64url

    (webcrypto ())
        .subtle.importKey (Raw, buffer.Buffer.from(kek, base64url), AesGcm.algorithm, false, [| Encrypt; Decrypt |])
    |> Async.AwaitPromise

let tests =
    testList "Node platform bindings (Fable.NodeExtras)" [

        // `Node.Buffer.BufferEncoding` is a closed StringEnum with no `base64url` case, so what
        // is under test is that the cast emits an encoding Node RECOGNISES — a wrong one is not
        // an error anywhere, it is base64 with the wrong alphabet or a silent fall back to utf8.
        testCase "base64url encodes over the URL-safe alphabet" <| fun () ->
            // These three bytes are four repeats of the sextet base64 spells `+` and base64url
            // spells `-`, so the two alphabets disagree on every character of the answer.
            let bytes = buffer.Buffer.from [| 0xFB; 0xEF; 0xBE |]
            Expect.equal (bytes.toString base64url) "----" "the URL-safe alphabet, not base64's"

        testCase "base64url decodes back to what it encoded" <| fun () ->
            let original = "a secret with ~ and ? in it"
            let encoded = buffer.Buffer.from(original, utf8).toString base64url
            let decoded = buffer.Buffer.from(encoded, base64url).toString utf8
            Expect.equal decoded original "the same encoding reads its own output back"

        // The reason a decoder is a value with a lifetime: without `{ stream: true }` each call
        // would answer U+FFFD for the half-character it was given, twice, and the text would be
        // wrong in a way only a multi-byte character at a chunk boundary reveals.
        testCase "a character split across two chunks decodes whole" <| fun () ->
            let eacute = JS.Constructors.Uint8Array.Create 2
            eacute[0] <- 0xC3uy
            eacute[1] <- 0xA9uy
            let decoder = createDecoder ()
            let first = decodeChunk decoder (eacute.subarray (0, 1))
            let second = decodeChunk decoder (eacute.subarray 1)
            Expect.equal (first + second) "é" "the tail was held back and completed"

        // `Fable.Fetch`'s `Response` has no `body` at all, so this is the only route from a
        // response to bytes that does not first wait for the whole of it. A `data:` URL is a
        // real fetch with a real stream and no port to bind.
        testCaseAsync "a response body reads to the end a chunk at a time" <| async {
            let! response =
                Fetch.fetchUnsafe "data:text/plain,hello%20stream" [] |> Async.AwaitPromise

            let reader = (responseBody response |> Option.get).getReader ()
            let decoder = createDecoder ()

            let rec drain (acc: string) =
                async {
                    let! chunk = reader.read () |> Async.AwaitPromise

                    if chunk.``done`` then
                        return acc
                    else
                        let text = chunk.value |> Option.map (decodeChunk decoder) |> Option.defaultValue ""
                        return! drain (acc + text)
                }

            let! text = drain ""
            Expect.equal text "hello stream" "every chunk arrived, and the end said so"
        }

        testCase "equal buffers compare equal" <| fun () ->
            let left = buffer.Buffer.from("the same secret", utf8)
            let right = buffer.Buffer.from("the same secret", utf8)
            Expect.isTrue (timingSafeEqual left right) "the same bytes compare equal"

        testCase "buffers differing in one byte compare unequal" <| fun () ->
            let left = buffer.Buffer.from("the same secret", utf8)
            let right = buffer.Buffer.from("the same secrer", utf8)
            Expect.isFalse (timingSafeEqual left right) "one byte apart is not equal"

        // Round-tripping proves the algorithm dictionaries reach WebCrypto in the shape it
        // reads: a missing `name` or `iv` throws, and a key imported through the wrong format
        // never imports at all.
        testCaseAsync "an AES-GCM ciphertext decrypts back under the same key and AAD" <| async {
            let! key = importedKey ()
            let iv = (webcrypto ()).getRandomValues (JS.Constructors.Uint8Array.Create 12)
            let aad = buffer.Buffer.from("yession-secret:1:session:token", utf8)

            let! ciphertext =
                (webcrypto ()).subtle.encrypt (AesGcm.parameters iv aad, key, buffer.Buffer.from("the plaintext", utf8))
                |> Async.AwaitPromise

            let! plaintext =
                (webcrypto ()).subtle.decrypt (AesGcm.parameters iv aad, key, buffer.Buffer.from ciphertext)
                |> Async.AwaitPromise

            Expect.equal (buffer.Buffer.from(plaintext).toString utf8) "the plaintext" "the round trip"
        }

        // And that the AAD is CARRIED rather than dropped on the way through the anonymous
        // record: a binding that lost `additionalData` would pass the round trip above and
        // silently stop binding a ciphertext to its entry.
        testCaseAsync "a ciphertext presented with another AAD is refused" <| async {
            let! key = importedKey ()
            let iv = (webcrypto ()).getRandomValues (JS.Constructors.Uint8Array.Create 12)
            let aad = buffer.Buffer.from("yession-secret:1:session:token", utf8)

            let! ciphertext =
                (webcrypto ()).subtle.encrypt (AesGcm.parameters iv aad, key, buffer.Buffer.from("the plaintext", utf8))
                |> Async.AwaitPromise

            let elsewhere = buffer.Buffer.from("yession-secret:1:session:other", utf8)

            let! outcome =
                (webcrypto ()).subtle.decrypt (AesGcm.parameters iv elsewhere, key, buffer.Buffer.from ciphertext)
                |> Async.AwaitPromise
                |> Async.Catch

            Expect.isTrue
                (match outcome with
                 | Choice1Of2 _ -> false
                 | Choice2Of2 _ -> true)
                "GCM authentication failed, as it must for an AAD that is not the one sealed with"
        }
    ]

// --- Spawning, and the members of a child Fable.Node does not declare ------------------------

/// Options with nothing in them, so each case below varies exactly the one field it is about.
/// An empty `Env` is not an omission: Node REPLACES the environment rather than merging it, and
/// a child spawned by absolute path needs nothing in it.
let private bare =
    { Cwd = None
      Env = Map.empty
      Stdio = Pipe
      Detached = false }

let private node (script: string) (arguments: string list) (options: SpawnOptions) =
    spawn ``process``.execPath ([ "-e"; script ] @ arguments) options

let portsTests =
    testList "Node platform bindings, spawning (Fable.NodeExtras)" [

        testCaseAsync "a child's exit code is readable once it has exited" <| async {
            let child = node "process.exit(3)" [] bare
            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 3) "the code the child chose"
        }

        // The options record is the whole reason this binding exists — Fable.Node types them
        // `obj` — so what is pinned is that each field arrives under the name Node reads it by.
        testCaseAsync "the environment given is the environment the child has" <| async {
            let child =
                node "process.exit(process.env.YESSION_MARK === 'set' ? 4 : 5)" [] { bare with Env = Map [ "YESSION_MARK", "set" ] }

            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 4) "the child saw the variable it was given"
        }

        testCaseAsync "the working directory given is where the child runs" <| async {
            let directory = os.tmpdir ()

            let child =
                node
                    "const fs = require('node:fs'); process.exit(fs.realpathSync(process.cwd()) === fs.realpathSync(process.argv[1]) ? 6 : 7)"
                    [ directory ]
                    { bare with Cwd = Some directory }

            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 6) "the child started where it was told to"
        }

        // `killed` says a signal was SENT, which is why it is true before the child is gone.
        testCaseAsync "a child is killed from the moment it is signalled" <| async {
            let child = node "setTimeout(() => {}, 60000)" [] bare
            child.kill "SIGKILL"
            let signalled = killed child
            let! _ = until (fun () -> (signalCode child).IsSome)
            Expect.isTrue signalled "the child reports the signal was sent"
        }

        // And the other half of the pair: a child a signal ended has no exit code to report, so
        // a caller reading only `exitCode` cannot tell it from one still running.
        testCaseAsync "a child a signal ended reports the signal rather than a code" <| async {
            let child = node "setTimeout(() => {}, 60000)" [] bare
            child.kill "SIGKILL"
            let! _ = until (fun () -> (signalCode child).IsSome)
            Expect.equal (signalCode child) (Some "SIGKILL") "the signal that ended it"
        }
    ]

// --- WebSocket ------------------------------------------------------------------------------

/// Connect, do something once the socket is open, and answer with the first frame that comes
/// back — then close, because the provider's `stop` waits for its connections.
///
/// The loopback provider is `Attach.startProvider`, which `docs/streams.md` points an outside
/// implementer at. Reused rather than re-written: a second hand-rolled RFC 6455 server in this
/// suite would be a second thing to keep in step with the spec.
let private firstFrame (url: string) (opened: WebSocket -> unit) : Async<Frame> =
    Async.FromContinuations (fun (resolve, _, _) ->
        let socket = connect url
        let mutable answered = false
        socket.binaryType <- ArrayBuffer

        socket.onMessage (fun event ->
            if not answered then
                answered <- true
                socket.close ()
                resolve (payload event))

        socket.onOpen (fun () -> opened socket))

let socketTests =
    testList "Node platform bindings, WebSocket (Fable.NodeExtras)" [

        testCaseAsync "a binary frame sent through the binding comes back as binary" <| async {
            let! provider = Attach.startProvider () |> Async.AwaitPromise

            let! data =
                firstFrame (sprintf "ws://127.0.0.1:%d/echo" provider.port) (fun socket ->
                    socket.sendBinary (buffer.Buffer.from("hello", utf8)))

            let seen =
                match data with
                | Frame.Text _ -> "a text frame"
                | Frame.Binary bytes -> buffer.Buffer.from(bytes).toString utf8

            Expect.equal seen "echo:hello" "the bytes went out and came back as bytes"
            do! provider.stop () |> Async.AwaitPromise
        }

        // The other arm of the union, and the distinction the wire is built on: text is CONTROL,
        // and a binding that answered every frame as one kind would erase it.
        testCaseAsync "a text frame arrives as text" <| async {
            let! provider = Attach.startProvider () |> Async.AwaitPromise
            let! data = firstFrame (sprintf "ws://127.0.0.1:%d/talkative" provider.port) ignore

            let seen =
                match data with
                | Frame.Text text -> text
                | Frame.Binary _ -> "binary"

            Expect.stringContains seen "from-a-later-version" "the control frame arrived as text"
            do! provider.stop () |> Async.AwaitPromise
        }
    ]
