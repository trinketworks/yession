module Yession.Tests.NodeExtras

// `src/Fable.NodeExtras` — the Node platform slice `Fable.Node` does not type — exercised
// against the real platform.
//
// A binding project that nothing calls is only TYPE-CHECKED, and type-checking an interop
// declaration proves almost nothing: an `[<Emit>]` string, an `[<Import>]` name and an
// anonymous record's field spelling are all opaque to the compiler and all wrong in exactly
// the way that compiles. So every binding declared there is called here at least once, on
// Node, in the cheapest tier that can host it — with two exceptions, each called somewhere
// better. The HTTP-client slice (`httpRequest`, `Readable`/`Writable`, `HttpMessage`) is what
// `app/GitGateway.fs` is built from, and the gateway's [Ports] suite drives it with a REAL git:
// gzipped posts, chunked answers, a push and a fetch streaming both ways. An echo server here
// would exercise strictly less of it, so what stays here is the one part of that slice no wire
// reaches — the sentence `StreamError.describe` makes of a failure.
//
// The second is the pair that only a real device can answer for. `createNetServer` /
// `boundPort` exist to ask the OS for a free port, and `openTty` opens the far end of a pty:
// the first is driven by the Jumpstarter tier, which spawns two processes onto the port it
// answers, and the second by the Serial tier against a real socat. A case here could construct
// either and learn nothing — a port nobody binds and a tty nobody reads are exactly the
// declarations that are wrong in the way that compiles.
//
// What each case pins is the binding's PROMISE rather than the platform's behaviour: that
// `base64url` reaches Buffer as an encoding, that a decoder held between chunks really holds
// the tail of a character, that `additionalData` arrives where the algorithm reads it. Node
// is not under test; the two lines of ours that stand between F# and Node are.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Fable.NodeExtras
open Node.Api
open Node.Buffer
// For `Interop.awaitPromise`. Every await below goes through it rather than
// `Async.AwaitPromise`, for the reason that seam exists — see its own comment: Fable's
// trampoline can hijack a workflow onto a `setTimeout` before `Async.AwaitPromise` has
// attached its rejection handler, and a promise that rejects inside that window kills the
// Node process. A `try/with` or an `Async.Catch` around the await cannot help, because the
// handler they install is not attached yet either.
open Yession.Host

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
    |> Interop.awaitPromise

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
                Fetch.fetchUnsafe "data:text/plain,hello%20stream" [] |> Interop.awaitPromise

            let reader = (responseBody response |> Option.get).getReader ()
            let decoder = createDecoder ()

            let rec drain (acc: string) =
                async {
                    let! chunk = reader.read () |> Interop.awaitPromise

                    if chunk.``done`` then
                        return acc
                    else
                        let text = chunk.value |> Option.map (decodeChunk decoder) |> Option.defaultValue ""
                        return! drain (acc + text)
                }

            let! text = drain ""
            Expect.equal text "hello stream" "every chunk arrived, and the end said so"
        }

        // The two byte types Node has, and the seam between them: WebCrypto fills and reads a
        // `Uint8Array`, while the only thing that encodes bytes as base64url is a `Buffer`.
        testCase "bufferOf carries a typed array's bytes into a Buffer" <| fun () ->
            let bytes = JS.Constructors.Uint8Array.Create 3
            bytes.[0] <- 0xFBuy
            bytes.[1] <- 0xEFuy
            bytes.[2] <- 0xBEuy
            // The same three bytes the base64url case above uses, so a Buffer that took them in
            // the wrong order, or took the array's fields instead, says so in the answer.
            Expect.equal ((bufferOf bytes).toString base64url) "----" "the same bytes, in order"

        testCase "bufferOf copies, so the array cannot change what it handed over" <| fun () ->
            // `Buffer.from(typedArray)` copies; `Buffer.from(arrayBuffer)` — one property
            // away — is a VIEW over the same memory. Which of the two this is decides whether
            // an IV encoded for the wire can still be rewritten by whoever minted it.
            let bytes = JS.Constructors.Uint8Array.Create 3
            bytes.[0] <- 0xFBuy
            bytes.[1] <- 0xEFuy
            bytes.[2] <- 0xBEuy
            let copied = bufferOf bytes
            bytes.[0] <- 0uy
            Expect.equal (copied.toString base64url) "----" "the copy is not the array's to change"

        testCase "bytesOf views the Buffer's own bytes" <| fun () ->
            // The other direction is a CAST — a Node Buffer already IS a Uint8Array — so the
            // result shares the Buffer's memory rather than copying it. That is what makes it
            // free, and it is only true while it stays a cast.
            let original = buffer.Buffer.from("yes", utf8)
            let view = bytesOf original
            view.[0] <- 0x6Euy
            Expect.equal (original.toString utf8) "nes" "the same memory, not a copy of it"

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
                |> Interop.awaitPromise

            let! plaintext =
                (webcrypto ()).subtle.decrypt (AesGcm.parameters iv aad, key, buffer.Buffer.from ciphertext)
                |> Interop.awaitPromise

            Expect.equal (buffer.Buffer.from(plaintext).toString utf8) "the plaintext" "the round trip"
        }

        // And that the AAD is CARRIED rather than dropped on the way through the anonymous
        // record: a binding that lost `additionalData` would pass the round trip above and
        // silently stop binding a ciphertext to its entry.
        //
        // This is the case the seam above is load-bearing for, because its decrypt is MEANT
        // to reject — so the window is not a rare coincidence here, it is every run, and
        // whether the process survives comes down to where the trampoline happened to be.
        // Off the seam it killed CI once with `Cipher job failed` and no failing assertion
        // anywhere, on a branch that had changed nothing but how much async ran first.
        testCaseAsync "a ciphertext presented with another AAD is refused" <| async {
            let! key = importedKey ()
            let iv = (webcrypto ()).getRandomValues (JS.Constructors.Uint8Array.Create 12)
            let aad = buffer.Buffer.from("yession-secret:1:session:token", utf8)

            let! ciphertext =
                (webcrypto ()).subtle.encrypt (AesGcm.parameters iv aad, key, buffer.Buffer.from("the plaintext", utf8))
                |> Interop.awaitPromise

            let elsewhere = buffer.Buffer.from("yession-secret:1:session:other", utf8)

            let! outcome =
                (webcrypto ()).subtle.decrypt (AesGcm.parameters iv elsewhere, key, buffer.Buffer.from ciphertext)
                |> Interop.awaitPromise
                |> Async.Catch

            Expect.isTrue
                (match outcome with
                 | Choice1Of2 _ -> false
                 | Choice2Of2 _ -> true)
                "GCM authentication failed, as it must for an AAD that is not the one sealed with"
        }

        // What a stream reports is an `Error` by convention only, and the sentence built from
        // it is read by a person — so the two answers are pinned apart. The ordinary one:
        testCase "a stream error describes as the message it carries" <| fun () ->
            let refused : StreamError = !!{| message = "connect ECONNREFUSED 127.0.0.1:1" |}
            Expect.equal
                (StreamError.describe refused)
                "connect ECONNREFUSED 127.0.0.1:1"
                "the message, not a rendering of the error object"

        // And the one the reader would otherwise see as `undefined` in the middle of a
        // sentence: `emit('error')` can carry anything, an absent value included.
        testCase "an error carrying no message still describes as something readable" <| fun () ->
            Expect.equal (StreamError.describe null) "nothing" "a value that is not there is said to be, in words"

        // `Thrown.describe` is the general case, and what a `throw` can carry is anything.
        // Each kind is pinned apart because each used to go through `String(x)`, whose answers
        // for two of these were `[object Object]` and the bare word `Error`.
        testCase "an object thrown describes as its JSON rather than as [object Object]" <| fun () ->
            Expect.equal (Thrown.describe (box {| code = 7 |})) """{"code":7}""" "the value, not the kind of thing it is"

        testCase "an error carrying no message describes as its name" <| fun () ->
            Expect.equal (Thrown.describe (box (Thrown.errorWith ""))) "Error" "the one word an empty error has"

        testCase "a number thrown describes as its digits" <| fun () ->
            Expect.equal (Thrown.describe (box 7)) "7" "the digits a person reads"
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
            let! provider = Attach.startProvider () |> Interop.awaitPromise

            let! data =
                firstFrame (sprintf "ws://127.0.0.1:%d/echo" provider.port) (fun socket ->
                    socket.sendBinary (buffer.Buffer.from("hello", utf8)))

            let seen =
                match data with
                | Frame.Text _ -> "a text frame"
                | Frame.Binary bytes -> buffer.Buffer.from(bytes).toString utf8

            Expect.equal seen "echo:hello" "the bytes went out and came back as bytes"
            do! provider.stop () |> Interop.awaitPromise
        }

        // The other arm of the union, and the distinction the wire is built on: text is CONTROL,
        // and a binding that answered every frame as one kind would erase it.
        testCaseAsync "a text frame arrives as text" <| async {
            let! provider = Attach.startProvider () |> Interop.awaitPromise
            let! data = firstFrame (sprintf "ws://127.0.0.1:%d/talkative" provider.port) ignore

            let seen =
                match data with
                | Frame.Text text -> text
                | Frame.Binary _ -> "binary"

            Expect.stringContains seen "from-a-later-version" "the control frame arrived as text"
            do! provider.stop () |> Interop.awaitPromise
        }
    ]

// --- Aborting, relaying, and what was thrown ----------------------------------------------------

/// `new AbortController()` — the FIRING end, which the product never holds: the signals it
/// sees arrive from the agent SDK. Declared here rather than in the binding for that reason,
/// and because a test of a listening binding needs something to make it listen to.
let eventTests =
    testList "Node platform bindings, aborting and relaying (Fable.NodeExtras)" [

        // There is deliberately no case for the macro's `{ once: true }`: a signal fires at
        // most once by the spec — `abort()` on an already-aborted controller returns without
        // notifying anyone — so a listener that stayed registered would behave identically.
        // What `once` buys is that a long-lived signal stops retaining handlers, and nothing
        // a test can observe tells that apart from the alternative.
        testCase "a handler hung on a signal runs when the signal fires" <| fun () ->
            let controller = abortController ()
            let mutable ran = 0
            controller.signal.onAbort (fun () -> ran <- ran + 1)
            controller.abort ()
            Expect.equal ran 1 "the abort reached the handler"

        // The half a listener cannot answer: registering after the fact never runs, so a
        // caller has to ask as well as listen.
        testCase "a signal that has already fired says so" <| fun () ->
            let controller = abortController ()
            Expect.isFalse controller.signal.aborted "nothing has fired"
            controller.abort ()
            Expect.isTrue controller.signal.aborted "and now it has"

        testCase "what the relay emits reaches the listener, arguments and all" <| fun () ->
            let relay = createRelay ()
            let seen = ResizeArray<obj * obj> ()
            relay.on ("exit", box (System.Func<obj, obj, unit> (fun code signal -> seen.Add (code, signal))))
            relay.emit ("exit", [| box 3; box null |])
            Expect.equal (List.ofSeq seen) [ box 3, box null ] "both arguments arrived, in order"

        // The promise the `obj` listener exists to keep: `off` can only remove the function
        // `on` was given, so a binding that adapted it on the way in would leak every
        // listener anybody tried to remove.
        testCase "a listener removed through the relay stops hearing" <| fun () ->
            let relay = createRelay ()
            let mutable heard = 0
            let listener = box (System.Func<obj, unit> (fun _ -> heard <- heard + 1))
            relay.on ("exit", listener)
            relay.emit ("exit", [| box 0 |])
            relay.off ("exit", listener)
            relay.emit ("exit", [| box 0 |])
            Expect.equal heard 1 "the listener heard the first and not the second"

        testCase "a listener registered once hears once" <| fun () ->
            let relay = createRelay ()
            let mutable heard = 0
            relay.once ("exit", box (System.Func<obj, unit> (fun _ -> heard <- heard + 1)))
            relay.emit ("exit", [| box 0 |])
            relay.emit ("exit", [| box 0 |])
            Expect.equal heard 1 "the second emit found no listener"

        // JavaScript admits a `throw` of any value, and F#'s `exn` is a class of Fable's own
        // that is NOT `instanceof Error` — which is the whole reason the question is asked
        // rather than assumed.
        testCase "an F# exception is not the platform's Error" <| fun () ->
            Expect.isFalse (isError (box (exn "boom"))) "Fable's Exception is its own class"
            Expect.isTrue (isError (box (errorWith "boom"))) "and `new Error` is not"

        // F#'s `string` answers "" for null, and `String()` answered the word `null`; a sentence
        // needs a word there, and not the platform's.
        testCase "nothing thrown describes as a word, not a blank" <| fun () ->
            Expect.equal (describe (box null)) "nothing" "said in words, where a blank would read as nothing having happened"

        testCase "an Error carries the message it was made with" <| fun () ->
            Expect.equal (errorWith "boom").Message "boom" "which is what a handler reads"
    ]

// --- Spawning with an environment this process did not build ------------------------------------

let seamTests =
    testList "Node platform bindings, a given environment (Fable.NodeExtras)" [

        // The promise `spawnWithEnv` exists for: the object handed in is the object the child
        // gets. A `Map` round trip would read as the same test and drop everything a map
        // cannot hold.
        testCaseAsync "the environment OBJECT given is the environment the child has" <| async {
            let env = Fable.Core.JsInterop.createObj [ "YESSION_MARK", box "set" ]

            let child =
                spawnWithEnv
                    ``process``.execPath
                    [ "-e"; "process.exit(process.env.YESSION_MARK === 'set' ? 4 : 5)" ]
                    env
                    None
                    Pipe
                    false

            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 4) "the child saw the variable the object carried"
        }

        // Node REPLACES rather than merges, and the seam's contract is that the child sees
        // exactly what it was given — so what this process holds must not leak into it.
        testCaseAsync "nothing of this process's own environment rides along" <| async {
            let child =
                spawnWithEnv
                    ``process``.execPath
                    [ "-e"; "process.exit(process.env.PATH === undefined ? 6 : 7)" ]
                    (Fable.Core.JsInterop.createObj [])
                    None
                    Pipe
                    false

            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 6) "an empty object is an empty environment"
        }

        testCaseAsync "the working directory given is where the child runs" <| async {
            let directory = os.tmpdir ()

            let child =
                spawnWithEnv
                    ``process``.execPath
                    [ "-e"
                      "const fs = require('node:fs'); process.exit(fs.realpathSync(process.cwd()) === fs.realpathSync(process.argv[1]) ? 8 : 9)"
                      directory ]
                    (Fable.Core.JsInterop.createObj [])
                    (Some directory)
                    Pipe
                    false

            let! _ = until (fun () -> (exitCode child).IsSome)
            Expect.equal (exitCode child) (Some 8) "the child started where it was told to"
        }
    ]
