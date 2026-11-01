module Yession.Tests.Secrets

// Plan 06, Step 1: the secrets vocabulary and the full ABAC decision table. Pure —
// cheap tier, every environment. The policy is default-deny: the table asserts every
// permitted cell explicitly and pins representative denies for each rule family, so a
// new Permit can only appear by editing the policy AND this table.

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Access
open Yession.Peer

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionA = SessionId.create "session-aa" |> expect
let private sessionB = SessionId.create "session-bb" |> expect
let private alice = UserId.create "alice" |> expect
let private bob = UserId.create "bob" |> expect
let private name = SecretName.create "deploy-token" |> expect
/// A browser peer — nobody the Manager verified, so nothing is owned by one.
let private peer1 = PeerId.create "browser-1" |> expect

/// A launch of session A that alice (and only alice) has signed in to.
let private callerA : Subject = { Session = Some sessionA; Users = Set.singleton alice; Local = false }
/// A launch of session A with no completed login.
let private callerANoUsers : Subject = { Session = Some sessionA; Users = Set.empty; Local = false }
/// A launch of session A whose login was UNATTRIBUTED — `--auth localhost`. It has a
/// subject (every launch does) but nobody was named behind it.
let private callerALocal : Subject = { Session = Some sessionA; Users = Set.singleton alice; Local = true }
/// A subject with no session at all (the future UI shape).
let private noSession : Subject = { Session = None; Users = Set.singleton alice; Local = false }

let private request subject action resource : Request =
    { Subject = subject; Access = SecretAccess (action, resource) }

let private permits msg r = Expect.equal (Policy.authorize r) Permit msg
let private denies msg r =
    match Policy.authorize r with
    | Deny _ -> ()
    | Permit -> failwithf "expected Deny: %s" msg

let private onOwn action = request callerA action (SecretResource { Scope = SessionScope sessionA; Name = name })
let private onSibling action = request callerA action (SecretResource { Scope = SessionScope sessionB; Name = name })
let private onUser subject user action = request subject action (SecretResource { Scope = UserScope user; Name = name })

let private constructorTests =
    testList "constructors" [
        testCase "UserId trims surrounding whitespace" <| fun () ->
            Expect.equal (UserId.create "  local  " |> expect |> UserId.value) "local" "trimmed"
        testCase "UserId rejects blank input" <| fun () ->
            Expect.isError (UserId.create "   ") "blank rejected"
        testCase "SecretScope.describe distinguishes scopes" <| fun () ->
            Expect.equal (SecretScope.describe (SessionScope sessionA)) "session:session-aa" "session form"
            Expect.equal (SecretScope.describe (UserScope alice)) "user:alice" "user form"
            // No key: the deployment IS the owner, so there is nothing to name after it.
            // This string is also the cipher AAD, so it is a stored-data contract.
            Expect.equal (SecretScope.describe LocalScope) "local" "local form"
    ]

let private policyTests =
    testList "policy decision table" [
        // A session owns its session-scoped secrets outright.
        testCase "own session scope: set/delete/inject/list permit" <| fun () ->
            permits "set" (onOwn SetSecret)
            permits "delete" (onOwn DeleteSecret)
            permits "inject" (onOwn InjectSecret)
            permits "list" (request callerA ListSecrets (SecretCollection (SessionScope sessionA)))

        // Nothing crosses sessions.
        testCase "sibling session scope: every action denies" <| fun () ->
            denies "set" (onSibling SetSecret)
            denies "delete" (onSibling DeleteSecret)
            denies "inject" (onSibling InjectSecret)
            denies "list" (request callerA ListSecrets (SecretCollection (SessionScope sessionB)))

        // User scope lists require the Manager-recorded binding.
        testCase "bound user: list permits" <| fun () ->
            permits "list" (request callerA ListSecrets (SecretCollection (UserScope alice)))

        testCase "unbound user: list denies" <| fun () ->
            denies "list other user" (request callerA ListSecrets (SecretCollection (UserScope bob)))

        // A `SecretRef` comes from a repository's file, not from the user: no consent
        // surface hands a user's secret to a session, so injection denies even when bound.
        testCase "user scope injection denies even for a bound user" <| fun () ->
            denies "inject" (onUser callerA alice InjectSecret)

        // Sessions never write user scope, binding or not.
        testCase "user scope writes deny even for a bound user" <| fun () ->
            denies "set" (onUser callerA alice SetSecret)
            denies "delete" (onUser callerA alice DeleteSecret)

        // Default-deny for shapes no rule matches.
        testCase "session-less subject denies on session scope" <| fun () ->
            denies "set without a session" (request noSession SetSecret (SecretResource { Scope = SessionScope sessionA; Name = name }))
        testCase "list of a RESOURCE (not a collection) has no rule and denies" <| fun () ->
            denies "list resource" (onOwn ListSecrets)
        testCase "write aimed at a COLLECTION has no rule and denies" <| fun () ->
            denies "set collection" (request callerA SetSecret (SecretCollection (SessionScope sessionA)))

        testCase "deny reasons never echo the secret name" <| fun () ->
            match Policy.authorize (onSibling SetSecret) with
            | Deny reason -> Expect.isFalse (reason.Contains "deploy-token") "reason is generic"
            | Permit -> failwith "expected Deny"
    ]

// Connection-credential rows (Plan 08): every action — including the WRITE — is
// permitted exactly where the caller IS the target scope's owner. This is the narrow
// family that lets a sign-in store an owner-scoped credential; the generic secret rows
// above (user-scope writes always deny) are unchanged.
let private connActions = [ ConnectCredential; ReadConnectionStatus; ResolveCredential; DisconnectCredential ]
let private onConnection subject action scope : Request =
    { Subject = subject; Access = ConnectionAccess (action, { Scope = scope; Name = name }) }

let private connectionPolicyTests =
    testList "connection policy rows" [
        testCase "own session scope: every connection action permits; a sibling's denies" <| fun () ->
            for action in connActions do
                permits (sprintf "%A own" action) (onConnection callerA action (SessionScope sessionA))
                denies (sprintf "%A sibling" action) (onConnection callerA action (SessionScope sessionB))

        testCase "bound user: every connection action permits; unbound denies" <| fun () ->
            for action in connActions do
                permits (sprintf "%A bound" action) (onConnection callerA action (UserScope alice))
                denies (sprintf "%A other user" action) (onConnection callerA action (UserScope bob))
                denies (sprintf "%A before login" action) (onConnection callerANoUsers action (UserScope alice))

        testCase "a session-less subject with a bound user permits (the future UI shape)" <| fun () ->
            permits "connect without a session" (onConnection noSession ConnectCredential (UserScope alice))

        testCase "a session-less subject cannot touch session scope" <| fun () ->
            denies "no session" (onConnection noSession ConnectCredential (SessionScope sessionA))

        testCase "local scope: a launch granted unattributed access permits; an attributed one denies" <| fun () ->
            for action in connActions do
                permits (sprintf "%A unattributed" action) (onConnection callerALocal action LocalScope)
                // callerA has a BOUND USER and still denies: what grants the deployment
                // credential is unattributed access, not merely having logged in.
                denies (sprintf "%A attributed" action) (onConnection callerA action LocalScope)

        testCase "local scope holds connection credentials only, never generic secrets" <| fun () ->
            for action in [ SetSecret; DeleteSecret; InjectSecret ] do
                denies (sprintf "%A on local" action) (request callerALocal action (SecretResource { Scope = LocalScope; Name = name }))
            denies "list local" (request callerALocal ListSecrets (SecretCollection LocalScope))

        testCase "CredentialOwner maps principals and scopes" <| fun () ->
            Expect.equal (CredentialOwner.ofPrincipal (Principal.User alice)) (Some (UserOwner alice)) "user"
            // A peer is nobody the Manager verified, so they own nothing of their own —
            // their turn falls through to whatever the DEPLOYMENT holds. The agent, the
            // process and the deployment are not principals at all, so they cannot be asked.
            Expect.equal (CredentialOwner.ofPrincipal (Principal.Peer peer1)) None "peer owns nothing"
            Expect.equal (CredentialOwner.scope (UserOwner alice)) (UserScope alice) "user scope"
            Expect.equal (CredentialOwner.scope LocalOwner) LocalScope "local scope"
    ]

// --- Records: the third family's policy rows ---------------------------------------------

let private recordOf (owner: CredentialOwner) (n: string) : RecordId =
    { RecordId.Owner = owner; RecordId.Name = RecordName.create n |> expect }
let private recordActions = [ ReadRecord; WriteRecord ]
let private onRecord subject action owner : Request =
    { Subject = subject; Access = RecordAccess (action, recordOf owner "repo-trust/v1/github/42") }

let private recordPolicyTests =
    testList "record policy rows" [
        testCase "a user signed in to the session: their records read and write" <| fun () ->
            for action in recordActions do
                permits (sprintf "%A bound" action) (onRecord callerA action (UserOwner alice))

        testCase "a user not signed in to the session: their records are refused" <| fun () ->
            for action in recordActions do
                denies (sprintf "%A other user" action) (onRecord callerA action (UserOwner bob))

        testCase "before anyone signs in, no user's records are reachable" <| fun () ->
            for action in recordActions do
                denies (sprintf "%A before login" action) (onRecord callerANoUsers action (UserOwner alice))

        testCase "the deployment's records: a launch granted unattributed access reads and writes" <| fun () ->
            for action in recordActions do
                permits (sprintf "%A unattributed" action) (onRecord callerALocal action LocalOwner)

        testCase "the deployment's records: an attributed launch is refused" <| fun () ->
            for action in recordActions do
                denies (sprintf "%A attributed" action) (onRecord callerA action LocalOwner)

        testCase "a record name of lowercase letters, digits and separators is accepted" <| fun () ->
            Expect.isOk (RecordName.create "repo-trust/v1/github_x.y-42") "accepted"

        testCase "a record name with a character outside the allowed set is refused" <| fun () ->
            Expect.isError (RecordName.create "repo trust") "a space is refused"

        testCase "a record name over the length limit is refused" <| fun () ->
            Expect.isError (RecordName.create (String.replicate (RecordName.maxLength + 1) "a")) "too long"
    ]

// --- Step 2: the envelope + wire codecs -------------------------------------------------

open Yession.Manager

let private entry scope entryName iv ct : SecretEntry =
    { Scope = scope
      Name = entryName
      CreatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z"
      UpdatedAt = DateTimeOffset.Parse "2026-07-24T11:00:00Z"
      Iv = iv
      Ciphertext = ct }

let private envelopeTests =
    testList "SecretsFile envelope" [
        testCase "round-trips through the codec" <| fun () ->
            let file =
                SecretsFile.empty "kek-1"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "aXY" "Y3Q")
                |> SecretsFile.upsert (entry (UserScope alice) name "aXYy" "Y3Qy")
                |> SecretsFile.upsertConnection (entry (UserScope alice) name "aXYz" "Y3Qz")
            let round = SecretsCodec.toString file |> SecretsCodec.fromString |> expect
            Expect.equal round file "identical after round-trip"

        testCase "unknown scope kind fails the decode" <| fun () ->
            let json = """{"version":1,"kekId":"k","entries":[{"scope":{"kind":"planet","sessionId":"x"},"name":"n","createdAt":"2026-07-24T10:00:00Z","updatedAt":"2026-07-24T10:00:00Z","iv":"a","ciphertext":"b"}]}"""
            Expect.isError (SecretsCodec.fromString json) "unknown kind rejected"

        testCase "corrupt JSON fails loudly" <| fun () ->
            Expect.isError (SecretsCodec.fromString "{ not json") "corrupt input rejected"

        testCase "upsert replaces by (scope, name) and keeps CreatedAt" <| fun () ->
            let first = entry (SessionScope sessionA) name "iv1" "ct1"
            let second = { entry (SessionScope sessionA) name "iv2" "ct2" with CreatedAt = DateTimeOffset.Parse "2026-07-24T12:00:00Z" }
            let file = SecretsFile.empty "k" |> SecretsFile.upsert first |> SecretsFile.upsert second
            Expect.equal file.Entries.Length 1 "replaced, not appended"
            Expect.equal file.Entries.Head.Ciphertext "ct2" "new ciphertext"
            Expect.equal file.Entries.Head.CreatedAt first.CreatedAt "creation timestamp survives"

        testCase "same name in different scopes are distinct entries" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (UserScope alice) name "iv2" "ct2")
            Expect.equal file.Entries.Length 2 "two entries"

        testCase "remove deletes exactly the identified entry" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (SessionScope sessionB) name "iv2" "ct2")
                |> SecretsFile.remove { Scope = SessionScope sessionA; Name = name }
            Expect.equal (file |> SecretsFile.tryFind { Scope = SessionScope sessionA; Name = name }) None "gone"
            Expect.isTrue (file |> SecretsFile.tryFind { Scope = SessionScope sessionB; Name = name } |> Option.isSome) "sibling untouched"

        testCase "list filters by scope and yields metadata only" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (UserScope alice) name "iv2" "ct2")
            let listed = SecretsFile.list (SessionScope sessionA) file
            Expect.equal listed.Length 1 "one entry in scope"
            Expect.equal listed.Head.Id.Scope (SessionScope sessionA) "right scope"
            // The metadata JSON carries no value/ciphertext field at all.
            let json = ControlWire.toString ControlWire.secretMetadata listed.Head
            Expect.isFalse (json.Contains "ct1") "no ciphertext in metadata"
            Expect.isFalse (json.Contains "value") "no value field in metadata"
    ]

let private wireTests =
    testList "control wire codecs" [
        testCase "set request round-trips" <| fun () ->
            let r : ControlWire.SetSecretRequest = { Scope = SessionScope sessionA; Name = name; Value = "s3cr3t" }
            let round = ControlWire.toString ControlWire.setSecretRequest r |> ControlWire.fromString ControlWire.setSecretRequest |> expect
            Expect.equal round r "identical"

        testCase "list request + response round-trip" <| fun () ->
            let req : ControlWire.ListSecretsRequest = { Scope = UserScope alice }
            Expect.equal (ControlWire.toString ControlWire.listSecretsRequest req |> ControlWire.fromString ControlWire.listSecretsRequest |> expect) req "request"
            let resp : ControlWire.ListSecretsResponse =
                { Secrets = [ { Id = { Scope = UserScope alice; Name = name }
                                CreatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z"
                                UpdatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z" } ] }
            Expect.equal (ControlWire.toString ControlWire.listSecretsResponse resp |> ControlWire.fromString ControlWire.listSecretsResponse |> expect) resp "response"

        testCase "delete request + response round-trip" <| fun () ->
            let req : ControlWire.DeleteSecretRequest = { Scope = SessionScope sessionA; Name = name }
            Expect.equal (ControlWire.toString ControlWire.deleteSecretRequest req |> ControlWire.fromString ControlWire.deleteSecretRequest |> expect) req "request"
            let resp : ControlWire.DeleteSecretResponse = { Deleted = true }
            Expect.equal (ControlWire.toString ControlWire.deleteSecretResponse resp |> ControlWire.fromString ControlWire.deleteSecretResponse |> expect) resp "response"

        testCase "blank secret name fails the request decode" <| fun () ->
            let json = """{"scope":{"kind":"session","sessionId":"session-aa"},"name":"   ","value":"v"}"""
            Expect.isError (ControlWire.fromString ControlWire.setSecretRequest json) "blank name rejected"

        testCase "every scope survives the wire, including the keyless one" <| fun () ->
            // `local` carries no key, so it is the one scope whose encoding could silently
            // lose information and still decode. This is a stored-file contract too — the
            // same codec writes `secrets.json`.
            for scope in [ SessionScope sessionA; UserScope alice; LocalScope ] do
                let r : ControlWire.DeleteSecretRequest = { Scope = scope; Name = name }
                let round = ControlWire.toString ControlWire.deleteSecretRequest r |> ControlWire.fromString ControlWire.deleteSecretRequest |> expect
                Expect.equal round r (sprintf "%A round-trips" scope)
            Expect.isError
                (ControlWire.fromString ControlWire.deleteSecretRequest """{"scope":{"kind":"everyone"},"name":"deploy-token"}""")
                "an unknown scope kind is refused, never guessed at"
    ]

// --- Step 3: cipher + store (real WebCrypto AES-GCM on Node; cheap tier) ----------------

open Fable.NodeExtras
open Node.Api
open Yession.Host

/// The one `crypto.subtle` member the product never calls and this suite must: exporting a
/// key, which is how non-extractability is pinned — by watching it refuse. Declared here, not
/// in `Fable.NodeExtras.SubtleCrypto`, because nothing in the product may ask it.
type private KeyExport =
    abstract exportKey : format: KeyFormat * key: CryptoKey -> Fable.Core.JS.Promise<Fable.Core.JS.ArrayBuffer>

[<Fable.Core.Import("subtle", "node:crypto")>]
let private subtle : KeyExport = Fable.Core.Util.jsNative

let private exportRawKey (key: CryptoKey) : Fable.Core.JS.Promise<Fable.Core.JS.ArrayBuffer> =
    subtle.exportKey (KeyFormat.Raw, key)

/// A durable store's two files, named fresh for each call.
let private freshFiles (label: string) : SecretStore.StoreFiles =
    let stem = sprintf "tests/Yession.Tests/out/.data/%s-%d" label (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
    { Secrets = stem + ".secrets.json"; Records = stem + ".records.json" }

let private sid (scope: SecretScope) (n: string) : SecretId =
    { Scope = scope; Name = SecretName.create n |> expect }

let private cipherTests =
    testList "cipher" [
        testCaseAsync "encrypt/decrypt round-trips under the right AAD" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = cipher.Encrypt aad "hunter2"
                let! back = cipher.Decrypt aad iv ct
                Expect.equal (expect back) "hunter2" "round-trips"
                Expect.isFalse (ct.Contains "hunter2") "ciphertext is not the plaintext"
            }

        testCaseAsync "a tampered ciphertext fails authentication" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = cipher.Encrypt aad "value"
                let tampered = (if ct.StartsWith "A" then "B" else "A") + ct.Substring 1
                let! r = cipher.Decrypt aad iv tampered
                Expect.isError r "tamper detected"
            }

        testCaseAsync "a ciphertext transplanted onto another entry's identity fails (AAD binding)" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! iv, ct = cipher.Encrypt (SecretsCipher.aadFor (UserScope alice) name) "alice's token"
                let! r = cipher.Decrypt (SecretsCipher.aadFor (SessionScope sessionA) name) iv ct
                Expect.isError r "transplant detected"
            }

        testCaseAsync "a different KEK cannot decrypt" <|
            async {
                let! sealer = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! other = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = sealer.Encrypt aad "value"
                let! r = other.Decrypt aad iv ct
                Expect.isError r "wrong key detected"
            }

        // The IV is minted per CALL, inside the operation, and never handed in: a repeat under
        // one key is the one thing GCM has no defence against, and a caller that COULD supply
        // an IV is the caller that eventually supplies the same one twice. What a repeat would
        // leak is exactly this: that two entries hold the same secret.
        testCaseAsync "the same plaintext encrypts differently every time" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! ivOne, ctOne = cipher.Encrypt aad "the same secret"
                let! ivTwo, ctTwo = cipher.Encrypt aad "the same secret"
                Expect.notEqual ivTwo ivOne "a fresh IV per call"
                Expect.notEqual ctTwo ctOne "so one plaintext is never twice the same ciphertext"
            }

        testCaseAsync "the IV is 96 bits, the size GCM is specified for" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! iv, _ = cipher.Encrypt (SecretsCipher.aadFor (SessionScope sessionA) name) "value"
                Expect.equal (int (buffer.Buffer.from(iv, base64url)).length) 12 "twelve bytes, base64url on the wire"
            }

        testCaseAsync "the imported KEK is non-extractable: exportKey rejects" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let mutable threw = false
                try
                    let! _ = exportRawKey cipher.Key |> Interop.awaitPromise
                    ()
                with _ -> threw <- true
                Expect.isTrue threw "exporting the KEK must throw"
            }
    ]

let private openDurable (files: SecretStore.StoreFiles) (keyStore: KeyStore.KeyStore) =
    async {
        let! opened = SecretStore.openStore (Some files) keyStore
        return expect (opened |> Result.mapError SecretStore.OpenError.describe)
    }

/// Open the in-memory ephemeral store (test double), stringifying the error kind.
let private openEphemeral () =
    async {
        let! opened = SecretStore.openStore None (KeyStore.random ())
        return expect (opened |> Result.mapError SecretStore.OpenError.describe)
    }

let private storeTests =
    testList "secret store" [
        testCaseAsync "set → list yields metadata; resolve returns the value; delete removes" <|
            async {
                let files = freshFiles "roundtrip"
                let! store = openDurable files (KeyStore.inMemory ())
                let id = sid (SessionScope sessionA) "deploy-token"
                let! set = store.Secrets.Set id "hunter2"
                Expect.equal (expect set).Id id "metadata identifies the secret"
                Expect.equal (store.Secrets.List (SessionScope sessionA) |> List.length) 1 "listed"
                let! resolved = store.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "resolves internally"
                let! deleted = store.Secrets.Delete id
                Expect.isTrue (expect deleted) "existed"
                let! resolvedAfter = store.Secrets.Resolve id
                Expect.equal (expect resolvedAfter) None "gone"
            }

        testCaseAsync "the KEK is durable in the key store before first use, and reopening decrypts" <|
            async {
                let files = freshFiles "reopen"
                let keyStore = KeyStore.inMemory ()
                let! store = openDurable files keyStore
                let! kek = keyStore.Get ()
                Expect.isTrue (expect kek |> Option.isSome) "KEK persisted at open, before any entry"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Secrets.Set id "hunter2"
                // A fresh store over the same file + key store (a Manager restart).
                let! reopened = openDurable files keyStore
                let! resolved = reopened.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "survives the restart"
                Expect.isTrue reopened.Durable "durable mode"
            }

        testCaseAsync "the value on disk is ciphertext only" <|
            async {
                let files = freshFiles "atrest"
                let! store = openDurable files (KeyStore.inMemory ())
                let! _ = store.Secrets.Set (sid (SessionScope sessionA) "deploy-token") "hunter2-at-rest"
                let onDisk = Fs.readText files.Secrets
                Expect.isFalse (onDisk.Contains "hunter2-at-rest") "no plaintext at rest"
                Expect.isTrue (onDisk.Contains "deploy-token") "metadata is cleartext by design"
            }

        testCaseAsync "a corrupt file fails the open loudly" <|
            async {
                let files = freshFiles "corrupt"
                Fs.writeTextAtomic files.Secrets "{ not json"
                let! opened = SecretStore.openStore (Some files) (KeyStore.inMemory ())
                Expect.isError opened "corrupt store must not look empty"
            }

        testCaseAsync "a file a newer build wrote is refused, not read as this build's newest" <|
            async {
                let files = freshFiles "newer"
                let file = SecretsFile.empty "k"
                Fs.writeTextAtomic files.Secrets ((SecretsCodec.toString file).Replace ("\"version\": 2", "\"version\": 3"))
                let! opened = SecretStore.openStore (Some files) (KeyStore.inMemory ())
                match opened with
                | Error (SecretStore.WrittenByNewerBuild (_, 3)) -> ()
                | other -> failwithf "expected WrittenByNewerBuild 3, got %A" other
            }

        testCaseAsync "a file sealed by a different key fails distinctly" <|
            async {
                let files = freshFiles "wrongkek"
                let! first = openDurable files (KeyStore.inMemory ())
                let! _ = first.Secrets.Set (sid (SessionScope sessionA) "deploy-token") "v"
                // A key store holding a different KEK (the first one is lost).
                let! reopened = SecretStore.openStore (Some files) (KeyStore.random ())
                match reopened with
                | Error (SecretStore.SealedByDifferentKey _ as e) ->
                    Expect.isTrue ((SecretStore.OpenError.describe e).Contains "sealed by a different key") "distinct wording"
                | Error other -> failwithf "expected SealedByDifferentKey, got %A" other
                | Ok _ -> failwith "expected the open to fail"
            }

        testCaseAsync "a key store with no key over a sealed file fails, and never mints or sets a key" <|
            async {
                // A locked macOS keychain reads as empty: the real KEK is still there, and
                // a minted one would overwrite it once the keychain unlocked.
                let files = freshFiles "lockedkeychain"
                let! first = openDurable files (KeyStore.inMemory ())
                let! _ = first.Secrets.Set (sid (SessionScope sessionA) "deploy-token") "v"
                let mutable sets = 0
                let empty = KeyStore.inMemory ()
                let counting = { empty with Set = fun value -> async { sets <- sets + 1; return! empty.Set value } }
                let! reopened = SecretStore.openStore (Some files) counting
                match reopened with
                | Error (SecretStore.KeyStoreFailure detail) ->
                    Expect.isTrue (detail.Contains "has no key") "says the key is missing"
                    Expect.isTrue (detail.Contains "locked keychain reads as empty") "names the locked keychain"
                | Error other -> failwithf "expected KeyStoreFailure, got %A" other
                | Ok _ -> failwith "expected the open to fail"
                Expect.equal sets 0 "the key store is never written"
                let! stillEmpty = empty.Get ()
                Expect.equal (expect stillEmpty) None "no key was minted"
            }

        testCaseAsync "a corrupt file with no key fails as corrupt without minting a key" <|
            async {
                let files = freshFiles "corruptnokey"
                Fs.writeTextAtomic files.Secrets "{ not json"
                let keyStore = KeyStore.inMemory ()
                let! opened = SecretStore.openStore (Some files) keyStore
                match opened with
                | Error (SecretStore.CorruptStore _) -> ()
                | other -> failwithf "expected CorruptStore, got %A" (other |> Result.map ignore)
                let! stored = keyStore.Get ()
                Expect.equal (expect stored) None "no key was minted"
            }

        testCaseAsync "the boot open waits out a key store with no key, and opens once the key is back" <|
            async {
                let files = freshFiles "waitforkey"
                let real = KeyStore.inMemory ()
                let! first = openDurable files real
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = first.Secrets.Set id "hunter2"
                let! kek = real.Get ()
                // Locked: reads as empty. Unlocked on the third read.
                let mutable reads = 0
                let mutable sets = 0
                let mutable waits = 0
                let locking =
                    { real with
                        Get = fun () -> async { reads <- reads + 1; return if reads < 3 then Ok None else Ok (expect kek) }
                        Set = fun value -> async { sets <- sets + 1; return! real.Set value } }
                let! opened = SecretStore.openStoreWaiting 1 (fun _ -> waits <- waits + 1) (Some files) locking
                let store = expect (opened |> Result.mapError SecretStore.OpenError.describe)
                let! resolved = store.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "the real key opened the file"
                Expect.isFalse store.KekMinted "loaded, not minted"
                Expect.equal reads 3 "retried until the key was there"
                Expect.equal waits 1 "the same failure is said once"
                Expect.equal sets 0 "the key store is never written"
            }

        testCaseAsync "the boot open does not wait on a corrupt file" <|
            async {
                let files = freshFiles "waitcorrupt"
                Fs.writeTextAtomic files.Secrets "{ not json"
                let! opened = SecretStore.openStoreWaiting 1 (fun _ -> failwith "must not wait") (Some files) (KeyStore.inMemory ())
                Expect.isError opened "fails at once"
            }

        testCaseAsync "the ephemeral store works without any file and reports non-durable" <|
            async {
                let! store = openEphemeral ()
                Expect.isFalse store.Durable "ephemeral"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Secrets.Set id "transient"
                let! resolved = store.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "transient") "usable in memory"
            }

        testCaseAsync "interleaved sets both land (encryption completes before the read-modify-write)" <|
            async {
                let files = freshFiles "interleave"
                let! store = openDurable files (KeyStore.inMemory ())
                let a = store.Secrets.Set (sid (SessionScope sessionA) "one") "1"
                let b = store.Secrets.Set (sid (SessionScope sessionA) "two") "2"
                let! _ = [ a; b ] |> Async.Parallel
                Expect.equal (store.Secrets.List (SessionScope sessionA) |> List.length) 2 "both entries present"
            }
    ]

// --- Step 4: the real OS credential manager ([Keyring]) ---------------------------------
// Runs only where the run declares a usable credential store: `check Keyring` drives
// the genuine Keychain / Credential Manager / Secret Service on a desktop, and
// self-wraps in a private D-Bus session + gnome-keyring in headless containers and CI.

let private keyringTests =
    testList "OS keyring round-trip" [
        testCaseAsync "the keyring-backed KeyStore probes available and round-trips the KEK payload" <|
            async {
                // A namespaced test entry so a real machine's product entry is never touched.
                let store = KeyStore.keyring "yession-test" ("roundtrip-" + Yession.Host.Interop.randomSecret ())
                let! available = store.Available ()
                Expect.isTrue available "this run declared Keyring, so the store must probe available"
                let! empty = store.Get ()
                Expect.equal (expect empty) None "starts absent"
                let payload = KeyStore.payload "kek-test" (SecretsCipher.generateKek ())
                let! set = store.Set payload
                expect set
                let! back = store.Get ()
                Expect.equal (expect back) (Some payload) "round-trips through the OS store"
            }

        testCaseAsync "a whole SecretStore runs durable over the real keyring" <|
            async {
                let keyStore = KeyStore.keyring "yession-test" ("store-" + Yession.Host.Interop.randomSecret ())
                let files = freshFiles "keyring"
                let! store = openDurable files keyStore
                Expect.isTrue store.Durable "durable over the OS store"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Secrets.Set id "keyring-held"
                // Reopen: the KEK comes back from the credential manager and decrypts.
                let! reopened = openDurable files keyStore
                let! resolved = reopened.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "keyring-held") "KEK survives via the OS store"
            }
    ]

// --- An operator's own key store (`--keystore exec:<helper>`) ---------------------------
// Spawns a process per operation, so `Ports`. The helper is a shell script written here:
// what the Manager sends it and what it may answer is the whole contract, so a fixture
// that speaks the protocol is the test of it.

/// A helper script under `out/.data`, executable, keeping its blob beside itself. The
/// `$#` guard pins half the protocol: the operation is the ONE argument, and the payload
/// never travels as another.
let private helperScript (label: string) (body: string) : string =
    let path =
        sprintf "tests/Yession.Tests/out/.data/helper-%s-%d.sh" label (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)
    TestFiles.write path (sprintf "#!/bin/sh\n[ $# -eq 1 ] || exit 9\nblob=\"$0.blob\"\n%s\n" body)
    TestFiles.makeExecutable path
    path

/// A helper that keeps the payload in a file: `get` prints it (nothing until a `set`), `set`
/// stores stdin. What a KMS or Vault helper does, without the KMS.
let private storingHelper (label: string) : string =
    helperScript
        label
        "case \"$1\" in\n  get) [ -f \"$blob\" ] && cat \"$blob\"; exit 0 ;;\n  set) cat > \"$blob\" ;;\n  *) exit 8 ;;\nesac"

let private execTests =
    testList "helper key store" [
        testCase "a --keystore spelling round-trips, and an unknown one is refused" <| fun () ->
            Expect.equal (KeyStore.KeyStoreSpec.ofName None) (Ok KeyStore.Keyring) "absent is the keyring"
            Expect.equal (KeyStore.KeyStoreSpec.ofName (Some "keyring")) (Ok KeyStore.Keyring) "and so is its name"
            Expect.equal
                (KeyStore.KeyStoreSpec.ofName (Some "exec:/usr/local/bin/kek"))
                (Ok (KeyStore.Exec "/usr/local/bin/kek"))
                "exec: carries the helper's path"
            for spec in [ KeyStore.Keyring; KeyStore.Exec "/opt/helper with space" ] do
                Expect.equal (KeyStore.KeyStoreSpec.ofName (Some (KeyStore.KeyStoreSpec.describe spec))) (Ok spec) "describe is what ofName reads"
            Expect.isError (KeyStore.KeyStoreSpec.ofName (Some "exec:")) "a helper needs a path"
            Expect.isError (KeyStore.KeyStoreSpec.ofName (Some "kms")) "an unknown store is refused, never defaulted"
            Expect.isError (KeyStore.KeyStoreSpec.ofName (Some "")) "and so is an empty one"

        testCaseAsync "the helper-backed KeyStore round-trips the payload on the pipes" <|
            async {
                let store = KeyStore.exec (storingHelper "roundtrip")
                let! available = store.Available ()
                Expect.isTrue available "a named store is never probed away"
                let! empty = store.Get ()
                Expect.equal (expect empty) None "starts absent"
                let payload = KeyStore.payload "kek-test" (SecretsCipher.generateKek ())
                let! set = store.Set payload
                expect set
                let! back = store.Get ()
                Expect.equal (expect back) (Some payload) "round-trips through the helper"
            }

        testCaseAsync "a whole SecretStore runs durable over a helper, and the key survives a reopen" <|
            async {
                let keyStore = KeyStore.exec (storingHelper "store")
                let files = freshFiles "exec"
                let! store = openDurable files keyStore
                Expect.isTrue store.Durable "durable over the helper"
                Expect.isTrue store.KekMinted "the Manager minted the key; the helper only kept it"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Secrets.Set id "helper-held"
                let! reopened = openDurable files keyStore
                Expect.isFalse reopened.KekMinted "the second open loaded it"
                let! resolved = reopened.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "helper-held") "KEK survives via the helper"
            }

        testCaseAsync "a helper that fails is a key store failure the open retries, not a host without one" <|
            async {
                // Fails until a marker exists — IAM credentials that are not minted yet at
                // boot, a KMS that throttles — then behaves. `openStoreWaiting` must wait it
                // out rather than refuse, and `onWait` is where the marker gets written.
                let helper =
                    helperScript
                        "flaky"
                        "ready=\"$0.ready\"\n[ -f \"$ready\" ] || { echo 'credentials not ready' >&2; exit 7; }\ncase \"$1\" in\n  get) [ -f \"$blob\" ] && cat \"$blob\"; exit 0 ;;\n  set) cat > \"$blob\" ;;\nesac"
                let store = KeyStore.exec helper
                let! available = store.Available ()
                Expect.isTrue available "not probed: the operator named it"
                let! failed = store.Get ()
                match failed with
                | Error detail ->
                    Expect.isTrue (detail.Contains "exited 7") (sprintf "the exit status is reported: %s" detail)
                    Expect.isTrue (detail.Contains "credentials not ready") (sprintf "and what the helper said: %s" detail)
                | Ok _ -> failwith "a failing helper must not read as a store"
                let mutable waited = 0
                let waitedOn (e: SecretStore.OpenError) =
                    waited <- waited + 1
                    Expect.equal (SecretStore.OpenError.kind e) "keystore" "the wait is for the key store"
                    TestFiles.write (helper + ".ready") ""
                let! opened = SecretStore.openStoreWaiting 1 waitedOn (Some (freshFiles "flaky")) store
                let store = expect (opened |> Result.mapError SecretStore.OpenError.describe)
                Expect.equal waited 1 "waited once, then the helper answered"
                Expect.isTrue store.Durable "and the store is durable"
            }

        testCaseAsync "a helper that answers nonsense is a key store failure, not a key" <|
            async {
                let store = KeyStore.exec (helperScript "garbage" "echo 'not a payload'")
                let! opened = SecretStore.openStore (Some (freshFiles "garbage")) store
                match opened with
                | Error (SecretStore.KeyStoreFailure detail) ->
                    Expect.isTrue (detail.Contains "malformed") (sprintf "the payload shape is checked: %s" detail)
                | other -> failwithf "expected KeyStoreFailure, got %A" other
            }

        testCaseAsync "a helper that is not there is a key store failure the boot can name" <|
            async {
                let store = KeyStore.exec "tests/Yession.Tests/out/.data/no-such-helper"
                let! got = store.Get ()
                match got with
                | Error detail -> Expect.isTrue (detail.Contains "did not run") (sprintf "says it never ran: %s" detail)
                | Ok _ -> failwith "a missing helper must not read as an empty store"
            }
    ]

// --- Step 7: injection reads the session's own scope only (cheap tier) ----------------

let private resolveIn (store: SecretStore.SecretStore) : SecretStore.ResolveSecret =
    SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store.Secrets

let private resolutionTests =
    testList "injection resolution" [
        testCaseAsync "the session's own secret resolves" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Secrets.Set (sid (SessionScope sessionA) "deploy-token") "from-session"
                let! got = resolveIn store sessionA name
                Expect.equal (expect got) "from-session" "the session's own secret"
            }

        testCaseAsync "a sibling session's secret does not resolve" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Secrets.Set (sid (SessionScope sessionB) "deploy-token") "b's"
                let! got = resolveIn store sessionA name
                Expect.isError got "another session's scope is not read"
            }

        // The repository file writes the reference; a user never chose to hand this value
        // to the session, so a binding does not make it reachable.
        testCaseAsync "a bound user's secret of the same name does not resolve" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Secrets.Set (sid (UserScope alice) "deploy-token") "alice's"
                let! got = resolveIn store sessionA name
                Expect.isError got "a user's scope is not read"
            }

        testCaseAsync "a deployment's local secret does not resolve" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Secrets.Set (sid LocalScope "deploy-token") "local"
                let! got = resolveIn store sessionA name
                Expect.isError got "LocalScope is not read"
            }

        // The Manager's environment is the operator's, not a secret store. PATH is set in
        // every process that runs this suite, so the name is certainly there to be read.
        testCaseAsync "a name set only in the Manager's environment is an error that names the secret" <|
            async {
                let! store = openEphemeral ()
                let! got = resolveIn store sessionA (SecretName.create "PATH" |> expect)
                match got with
                | Error e -> Expect.isTrue (e.Contains "'PATH'") (sprintf "names the secret: %s" e)
                | Ok _ -> failwith "read the Manager's environment"
            }

        testCaseAsync "each resolution reports its outcome to the observer" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Secrets.Set (sid (SessionScope sessionA) "deploy-token") "v"
                let mutable observed : (string * bool) list = []
                let observe _ n outcome =
                    let injected =
                        match outcome with
                        | SecretStore.SecretResolution.Injected -> true
                        | SecretStore.SecretResolution.InjectMissed _ -> false
                    observed <- (SecretName.value n, injected) :: observed
                let resolve = SecretStore.SecretResolution.compose observe store.Secrets
                let! _ = resolve sessionA name
                let! _ = resolve sessionA (SecretName.create "OTHER" |> expect)
                Expect.equal (List.rev observed) [ "deploy-token", true; "OTHER", false ] "one observation per resolution"
            }
    ]

// --- The two families: a SecretRef never reaches a connection credential ---------------

let private githubName = SecretName.create "github" |> expect
let private githubAt scope : ConnectionId = { Scope = scope; Name = githubName }
let private brokerOver (store: SecretStore.SecretStore) =
    Broker.create (fun () -> "http://m/cb") store.Connections ignore Broker.asking

/// A version 1 file at `path`, sealed under the key `keyStore` holds: entries as a Manager
/// wrote them before the store split its families, every one under the generic AAD.
let private writeV1 (path: string) (keyStore: KeyStore.KeyStore) (entries: (SecretScope * SecretName * string) list) =
    async {
        let! raw = keyStore.Get ()
        let kekId, kek = raw |> Result.bind (function Some p -> KeyStore.splitPayload p | None -> Error "no key") |> expect
        let! cipher = SecretsCipher.importKey kek
        let mutable sealedEntries = []
        for scope, entryName, value in entries do
            let! iv, ct = cipher.Encrypt (SecretsCipher.aadFor scope entryName) value
            sealedEntries <- sealedEntries @ [ entry scope entryName iv ct ]
        let file = { SecretsFile.empty kekId with Version = 1; Entries = sealedEntries }
        Fs.writeTextAtomic path (SecretsCodec.toString file)
    }

let private familyTests =
    testList "the two families" [
        // The type already refuses an `InjectSecret` that names a `ConnectionId`; this pins
        // the store: a reference of the same scope and name still finds nothing.
        testCaseAsync "a SecretRef named like a session's connection credential does not resolve it" <|
            async {
                let! store = openEphemeral ()
                let! put = (brokerOver store).Put (githubAt (SessionScope sessionA)) "ghp_session"
                expect put
                let! got = resolveIn store sessionA githubName
                Expect.isError got "a connection credential is not a secret"
            }

        testCaseAsync "set_secret under a connection's name leaves the connection credential as it was" <|
            async {
                let! store = openEphemeral ()
                let broker = brokerOver store
                let! put = broker.Put (githubAt (SessionScope sessionA)) "ghp_session"
                expect put
                let api = ProcessManager.secretsApiFor ignore (resolveIn store) store.Secrets
                let! set =
                    api.Set
                        { SessionId = sessionA; Users = Set.empty; Local = false }
                        { Scope = SessionScope sessionA; Name = githubName; Value = "agent-written" }
                Expect.isOk set "the session's own generic secret is written"
                let! resolved = broker.Resolve (githubAt (SessionScope sessionA))
                Expect.equal (expect resolved |> snd) "ghp_session" "the broker reads its own credential"
            }

        testCaseAsync "a ciphertext moved between families fails authentication" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! iv, ct = cipher.Encrypt (SecretsCipher.aadFor (SessionScope sessionA) githubName) "v"
                let! moved = cipher.Decrypt (SecretsCipher.connectionAadFor (githubAt (SessionScope sessionA))) iv ct
                Expect.isError moved "the connection AAD does not open a generic ciphertext"
            }

        testCaseAsync "a version 1 file's broker envelope opens as a connection credential" <|
            async {
                let files = freshFiles "v1-connection"
                let keyStore = KeyStore.random ()
                let envelope = BrokeredCredentialCodec.toString (BrokeredStatic "ghp_kept")
                do! writeV1 files.Secrets keyStore [ UserScope alice, githubName, envelope ]
                let! store = openDurable files keyStore
                let! resolved = (brokerOver store).Resolve (githubAt (UserScope alice))
                Expect.equal (expect resolved |> snd) "ghp_kept" "the credential survives the split"
            }

        testCaseAsync "a version 1 file's broker envelope is no longer a generic secret" <|
            async {
                let files = freshFiles "v1-not-generic"
                let keyStore = KeyStore.random ()
                let envelope = BrokeredCredentialCodec.toString (BrokeredStatic "ghp_kept")
                do! writeV1 files.Secrets keyStore [ SessionScope sessionA, githubName, envelope ]
                let! store = openDurable files keyStore
                let! got = resolveIn store sessionA githubName
                Expect.isError got "a reference does not reach it"
            }

        testCaseAsync "a version 1 file's generic secret stays generic" <|
            async {
                let files = freshFiles "v1-generic"
                let keyStore = KeyStore.random ()
                do! writeV1 files.Secrets keyStore [ SessionScope sessionA, name, "hunter2" ]
                let! store = openDurable files keyStore
                let! got = resolveIn store sessionA name
                Expect.equal (expect got) "hunter2" "the session's secret resolves as before"
            }

        testCaseAsync "a sorted version 1 file opens again under this build's rules" <|
            async {
                let files = freshFiles "v1-reopen"
                let keyStore = KeyStore.random ()
                let envelope = BrokeredCredentialCodec.toString (BrokeredStatic "ghp_kept")
                do! writeV1 files.Secrets keyStore [ UserScope alice, githubName, envelope ]
                let! _ = openDurable files keyStore
                let! reopened = openDurable files keyStore
                let! resolved = (brokerOver reopened).Resolve (githubAt (UserScope alice))
                Expect.equal (expect resolved |> snd) "ghp_kept" "the written-back file holds the credential"
            }
    ]

// --- Audit rendering ----------------------------------------------------------------------

let private auditTests =
    testList "audit records" [
        testCase "a connection-action deny renders through the shared record" <| fun () ->
            let record =
                SecretStore.Audit.authzDeny
                    sessionA
                    (ConnectionAccess (ConnectCredential, { Scope = UserScope alice; Name = name }))
                    "user is not signed in to this session"
            Expect.isTrue ((SecretStore.Audit.format record).Contains "ConnectCredential") "names the inner action"
    ]

// --- Step 6: the secrets control routes over real HTTP ([Ports]) ------------------------
// A bare control server (the Phase4 pattern) with a known caller table and the SAME
// pre-authorized handlers the Manager composes (ProcessManager.secretsApiFor), driven
// as raw HTTP so denied shapes below the typed client are expressible.

/// A POST on a control route, under the shared secret that gates them.
let private postControl (url: string) (secret: string) (body: string) : Async<TestHttp.Reply> =
    TestHttp.post [ "x-yession-control", secret ] "application/json" body url

let private startControlServerWith
    (callers: (string * Control.ControlCaller) list)
    (api: Control.SecretsApi option)
    (records: Control.RecordsApi option)
    (onUnauthorized: string -> unit)
    =
    async {
        let table = Map.ofList callers
        let dummyRegister (_: string) (_: SessionId) (_: string) : Yession.Oidc.RegisterClientResponse =
            { ClientId = "unused"; ClientSecret = "unused"; Issuer = "unused" }
        let handler (req: IncomingMessage) (res: ServerResponse) =
            if not (Control.tryHandle
                        (fun secret -> Map.tryFind secret table)
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> Subscription.none)
                        (fun _ _ _ -> Subscription.none)
                        dummyRegister
                        api
                        None
                        records
                        (fun _ _ -> Subscription.none)
                        (fun _ _ -> "")
                        (fun _ _ -> false)
                        onUnauthorized
                        req res) then
                res.writeHead (404, [ ResponseHeader.ContentType "text/plain" ])
                res.``end`` "not found"
        let server = createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return listening, sprintf "http://127.0.0.1:%d" (serverPort listening)
    }

let private startControlServer (callers: (string * Control.ControlCaller) list) (api: Control.SecretsApi option) (onUnauthorized: string -> unit) =
    startControlServerWith callers api None onUnauthorized

let private caller sessionId users : Control.ControlCaller =
    { SessionId = sessionId; Users = users; Local = false }

/// The pre-authorized handlers over a store, with the SAME resolver the Manager composes
/// for `resolve`.
let private apiOver (audit: SecretStore.Audit.Sink) (store: SecretStore.SecretStore) : Control.SecretsApi =
    ProcessManager.secretsApiFor audit (resolveIn store) store.Secrets

let private routeTests =
    testList "control routes" [
        testCaseAsync "a session writes, lists (metadata only), and deletes its own scope; strangers and siblings are refused" <|
            async {
                let! store = openEphemeral ()
                // Recording sinks: every audit record and every 401 path this run emits.
                let mutable audited : SecretStore.Audit.Record list = []
                let mutable unauthorized : string list = []
                let! _, url =
                    startControlServer
                        [ "secret-a", caller sessionA (Set.singleton alice)
                          "secret-b", caller sessionB Set.empty ]
                        (Some (apiOver (fun r -> audited <- r :: audited) store))
                        (fun path -> unauthorized <- path :: unauthorized)
                let post route secret body = postControl (sprintf "%s/control/secrets/%s" url route) secret body
                let eventNames () =
                    audited
                    |> List.rev
                    |> List.choose (fun r ->
                        match Map.tryFind "event.name" r.Attributes with
                        | Some (SecretStore.Audit.StringValue n) -> Some n
                        | _ -> None)

                // Own scope: the full lifecycle.
                let setBody = ControlWire.toString ControlWire.setSecretRequest { Scope = SessionScope sessionA; Name = name; Value = "hunter2" }
                let! set = post "set" "secret-a" setBody
                Expect.equal set.Status 200 "own-scope set permits"
                Expect.isFalse (set.Body.Contains "hunter2") "the set response carries no value"
                let! listed = post "list" "secret-a" (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                Expect.equal listed.Status 200 "own-scope list permits"
                Expect.isTrue (listed.Body.Contains "deploy-token") "metadata names the secret"
                Expect.isFalse (listed.Body.Contains "hunter2") "the raw list body carries no value"

                // An unknown secret is turned away at the door.
                let! unknown = post "set" "not-a-secret" setBody
                Expect.equal unknown.Status 401 "invalid control secret"

                // Session B cannot touch A's scope (and the deny does not echo values).
                let! cross = post "set" "secret-b" setBody
                Expect.equal cross.Status 403 "cross-session write denies"
                let! crossList = post "list" "secret-b" (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                Expect.equal crossList.Status 403 "cross-session list denies"

                // User scope: sessions never write; a bound user's collection lists.
                let! userWrite = post "set" "secret-a" (ControlWire.toString ControlWire.setSecretRequest { Scope = UserScope alice; Name = name; Value = "v" })
                Expect.equal userWrite.Status 403 "sessions cannot write user scope"
                let! userList = post "list" "secret-a" (ControlWire.toString ControlWire.listSecretsRequest { Scope = UserScope alice })
                Expect.equal userList.Status 200 "a bound user's collection lists"
                let! unboundList = post "list" "secret-b" (ControlWire.toString ControlWire.listSecretsRequest { Scope = UserScope alice })
                Expect.equal unboundList.Status 403 "an unbound session cannot list a user's collection"

                // There is no read-back route, for anyone.
                let! get = post "get" "secret-a" setBody
                Expect.equal get.Status 404 "/control/secrets/get does not exist"

                // Delete closes the lifecycle.
                let! deleted = post "delete" "secret-a" (ControlWire.toString ControlWire.deleteSecretRequest { Scope = SessionScope sessionA; Name = name })
                Expect.equal deleted.Status 200 "own-scope delete permits"
                Expect.isTrue (deleted.Body.Contains "true") "reports the entry existed"

                // The audit trail (Plan 06 telemetry): permitted ops and every deny
                // became records; the 401 hook saw the path; no formatted record leaks
                // the value.
                Expect.equal
                    (eventNames ())
                    [ "yession.secret.set"; "yession.secret.list"        // own-scope set + list
                      "yession.authz.deny"; "yession.authz.deny"        // session B's two cross-scope attempts
                      "yession.authz.deny"                              // user-scope write
                      "yession.secret.list"                             // bound user's list
                      "yession.authz.deny"                              // unbound user list
                      "yession.secret.delete" ]
                    "one audit record per authorization decision, in order"
                Expect.equal unauthorized [ "/control/secrets/set" ] "the 401 hook carries the request path"
                for r in audited do
                    Expect.isFalse ((SecretStore.Audit.format r).Contains "hunter2") "no audit record ever renders a value"
            }

        testCaseAsync "resolve-at-spawn reads only the caller's own scope, over the authenticated channel" <|
            async {
                let! store = openEphemeral ()
                let! _, url =
                    startControlServer
                        [ "secret-a", caller sessionA (Set.singleton alice)
                          "secret-b", caller sessionB Set.empty ]
                        (Some (apiOver (fun _ -> ()) store))
                        ignore
                let userName = SecretName.create "user-held-token" |> expect
                let! _ = store.Secrets.Set { Scope = SessionScope sessionA; Name = name } "session-held"
                let! _ = store.Secrets.Set { Scope = UserScope alice; Name = userName } "user-held"
                let post secret body = postControl (url + "/control/secrets/resolve") secret body
                let resolveBody = ControlWire.toString ControlWire.resolveSecretRequest { Name = name }

                // The caller's own scope resolves — this is the ONE value-returning
                // secrets route, feeding env injection at the session's sandbox spawn.
                let! own = post "secret-a" resolveBody
                Expect.equal own.Status 200 "own-scope resolve permits"
                Expect.isTrue (own.Body.Contains "session-held") "the value crosses the authenticated channel"

                // A bound user's scope is not: the reference comes from a repository's file.
                let! bound = post "secret-a" (ControlWire.toString ControlWire.resolveSecretRequest { Name = userName })
                Expect.equal bound.Status 403 "a bound user's secret does not resolve"

                // A sibling session does not reach A's scope, and the deny echoes no value.
                let! cross = post "secret-b" resolveBody
                Expect.equal cross.Status 403 "another session's secret does not resolve"
                Expect.isFalse (cross.Body.Contains "session-held") "the deny carries no value"

                // An unknown control secret is turned away at the door.
                let! unknown = post "stolen" resolveBody
                Expect.equal unknown.Status 401 "invalid control secret"

                // The typed session-side client (what sandbox spawn uses) round-trips.
                let! typed = ControlClient.resolveSecret (ControlEndpoint.Address url) "secret-a" name
                Expect.equal (expect typed) "session-held" "the typed client resolves the value"
            }

        testCaseAsync "the session-side typed capability drives the full lifecycle over the wire" <|
            async {
                let! store = openEphemeral ()
                let! _, url =
                    startControlServer
                        [ "secret-a", caller sessionA Set.empty ]
                        (Some (apiOver (fun _ -> ()) store))
                        ignore
                // The capability a Session builds in SessionMain: pre-bound to
                // its own scope; failures are values.
                let capability = ControlClient.secretsCapabilities (ControlEndpoint.Address url) "secret-a" sessionA
                let! set = capability.SetSecret name "hunter2"
                Expect.equal (expect set).Id { Scope = SessionScope sessionA; Name = name } "metadata round-trips"
                let! listed = capability.ListSecrets ()
                Expect.equal (expect listed |> List.length) 1 "lists the one secret"
                let! deleted = capability.DeleteSecret name
                Expect.isTrue (expect deleted) "reports existence on delete"
                let! deletedAgain = capability.DeleteSecret name
                Expect.isFalse (expect deletedAgain) "second delete reports absence"
            }

        testCaseAsync "the agent capability surface denies cleanly without a grant" <|
            async {
                // What a turn sees when no secrets capability is threaded (Host passes
                // None): every operation is a legible Error value, never an exception.
                let! set = AgentCapabilities.none.Secrets.Set name "v"
                Expect.isError set "set denies"
                let! listed = AgentCapabilities.none.Secrets.List ()
                Expect.isError listed "list denies"
                let! deleted = AgentCapabilities.none.Secrets.Delete name
                Expect.isError deleted "delete denies"
            }

        testCaseAsync "a Manager without a secrets store answers 403" <|
            async {
                let! _, url = startControlServer [ "secret-a", caller sessionA Set.empty ] None ignore
                let! reply =
                    postControl
                        (url + "/control/secrets/list")
                        "secret-a"
                        (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                   
                Expect.equal reply.Status 403 "no store configured"
            }
    ]

// --- `--secrets`: what the operator asked for, and what this host can give ---------------
// The resolution is pure once the credential-manager probe is passed in, so the whole
// matrix runs in the cheap tier without a keyring.

let private backingName (backing: ProcessManager.SecretsBacking) =
    // DurableSecrets carries a record of FUNCTIONS: compare its name, never the value.
    match backing with
    | ProcessManager.DurableSecrets store -> "durable:" + store.Name
    | ProcessManager.EphemeralSecrets ProcessManager.OperatorChose -> "ephemeral:operator"
    | ProcessManager.EphemeralSecrets ProcessManager.NoCredentialManager -> "ephemeral:no-credential-manager"

let private resolves (mode: ProcessManager.SecretsMode) keyStore expected =
    match ProcessManager.SecretsBacking.forMode mode keyStore with
    | Ok backing -> Expect.equal (backingName backing) expected "resolved backing"
    | Error e -> failwithf "expected %s, got Error: %s" expected e

let private secretsModeTests =
    testList "secrets mode" [
        testCase "mode names resolve exactly, and an unknown one refuses the boot" <| fun () ->
            Expect.equal (ProcessManager.SecretsMode.ofName None) (Ok ProcessManager.AutoSecrets) "absent = no choice"
            Expect.equal (ProcessManager.SecretsMode.ofName (Some "durable")) (Ok ProcessManager.RequireDurable) "durable"
            Expect.equal (ProcessManager.SecretsMode.ofName (Some "ephemeral")) (Ok ProcessManager.ForceEphemeral) "ephemeral"
            // No `auto` spelling: absence is the only way to decline the choice.
            Expect.isError (ProcessManager.SecretsMode.ofName (Some "auto")) "auto is not a mode"
            Expect.isError (ProcessManager.SecretsMode.ofName (Some "in-memory")) "unknown name refused"

        testCase "the resolution matrix: what each mode does on a host with and without a credential manager" <| fun () ->
            let keyStore = Some (KeyStore.inMemory ())
            resolves ProcessManager.AutoSecrets keyStore "durable:in-memory"
            resolves ProcessManager.AutoSecrets None "ephemeral:no-credential-manager"
            resolves ProcessManager.RequireDurable keyStore "durable:in-memory"
            // The opt-out actually opts out: a credential manager IS available and is
            // deliberately not used.
            resolves ProcessManager.ForceEphemeral keyStore "ephemeral:operator"
            resolves ProcessManager.ForceEphemeral None "ephemeral:operator"

        testCase "only a mode that could USE a credential manager pays to look for one" <| fun () ->
            // The probe reaches for the platform credential store, which can be slow and can
            // prompt. An operator who asked for an in-memory store has already declined it.
            Expect.isTrue (ProcessManager.SecretsMode.needsCredentialManager ProcessManager.AutoSecrets) "auto might use one"
            Expect.isTrue (ProcessManager.SecretsMode.needsCredentialManager ProcessManager.RequireDurable) "durable demands one"
            Expect.isFalse (ProcessManager.SecretsMode.needsCredentialManager ProcessManager.ForceEphemeral) "ephemeral never uses one"
            // And skipping it cannot change the answer — `forMode` resolves ForceEphemeral
            // the same way whether or not a key store was found.
            resolves ProcessManager.ForceEphemeral None "ephemeral:operator"
            resolves ProcessManager.ForceEphemeral (Some (KeyStore.inMemory ())) "ephemeral:operator"

        testCase "--secrets durable refuses the boot rather than running a store that dies" <| fun () ->
            // Asking for a capability this host cannot offer is an error, never a silent
            // downgrade — the same rule the test tiers hold.
            Expect.isError
                (ProcessManager.SecretsBacking.forMode ProcessManager.RequireDurable None)
                "no credential manager, so durable is refused"

        testCaseAsync "a durable store outlives the process that wrote it" <|
            async {
                // The other half of "connect once": the KEK is stable, so a Manager that
                // restarts opens the same entries rather than an empty store.
                let files = freshFiles "restart"
                let keyStore = KeyStore.inMemory ()
                let id = sid LocalScope "claude-code"
                let! before = openDurable files keyStore
                let! _ = before.Secrets.Set id "token-value"
                let! after = openDurable files keyStore
                let! resolved = after.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "token-value") "the entry survived the reopen"
                Expect.equal after.EntriesAtOpen 1 "and was there at open, not written again"
            }
    ]

// --- Records: the third family's store, wire and routes ----------------------------------

let private aliceRecord = recordOf (UserOwner alice) "repo-trust/v1/github/42"

/// A store holding one record of alice's, at revision 1.
let private storeWithRecord (value: string) =
    async {
        let! store = openEphemeral ()
        let! written = store.Records.Write aliceRecord None value
        expect written |> ignore
        return store
    }

/// A records file a newer build wrote, beside no secrets file.
let private newerRecordsFile (files: SecretStore.StoreFiles) =
    Fs.writeTextAtomic files.Records "{ \"version\": 2, \"kekId\": \"k\", \"records\": [] }"

let private recordStoreTests =
    testList "records" [
        testCaseAsync "a written record reads back with its value and first revision" <|
            async {
                let! store = storeWithRecord "first"
                let! read = store.Records.Read aliceRecord
                Expect.equal (expect read |> Option.map (fun r -> r.Value, r.Revision)) (Some ("first", 1)) "read back"
            }

        testCaseAsync "a record nobody wrote reads as none" <|
            async {
                let! store = openEphemeral ()
                let! read = store.Records.Read aliceRecord
                Expect.equal (expect read) None "no record"
            }

        testCaseAsync "a write at the stored revision replaces the record" <|
            async {
                let! store = storeWithRecord "first"
                let! written = store.Records.Write aliceRecord (Some 1) "second"
                Expect.equal (expect written) (RecordWrite.Written 2) "the next revision"
            }

        testCaseAsync "a write at an older revision is told the stored one" <|
            async {
                let! store = storeWithRecord "first"
                let! _ = store.Records.Write aliceRecord (Some 1) "second"
                let! late = store.Records.Write aliceRecord (Some 1) "late"
                Expect.equal (expect late) (RecordWrite.Stale (Some 2)) "stale"
            }

        testCaseAsync "a stale write leaves the record as it was" <|
            async {
                let! store = storeWithRecord "first"
                let! _ = store.Records.Write aliceRecord None "late"
                let! read = store.Records.Read aliceRecord
                Expect.equal (expect read |> Option.map (fun r -> r.Value)) (Some "first") "unchanged"
            }

        testCaseAsync "a write that expects no record, over one that exists, is stale" <|
            async {
                let! store = storeWithRecord "first"
                let! second = store.Records.Write aliceRecord None "second"
                Expect.equal (expect second) (RecordWrite.Stale (Some 1)) "stale"
            }

        testCaseAsync "a value over the length limit is refused" <|
            async {
                let! store = openEphemeral ()
                let! written =
                    store.Records.Write aliceRecord None (String.replicate (SecretStore.RecordLimits.maxValueLength + 1) "x")
                Expect.isError written "refused"
            }

        testCaseAsync "an owner at the record limit cannot add another" <|
            async {
                let! store = openEphemeral ()
                for i in 1 .. SecretStore.RecordLimits.maxRecordsPerOwner do
                    let! written = store.Records.Write (recordOf (UserOwner alice) (sprintf "r/%d" i)) None "v"
                    expect written |> ignore
                let! extra = store.Records.Write (recordOf (UserOwner alice) "r/extra") None "v"
                Expect.isError extra "refused"
            }

        testCaseAsync "an owner at the record limit can still replace one of theirs" <|
            async {
                let! store = openEphemeral ()
                for i in 1 .. SecretStore.RecordLimits.maxRecordsPerOwner do
                    let! written = store.Records.Write (recordOf (UserOwner alice) (sprintf "r/%d" i)) None "v"
                    expect written |> ignore
                let! replaced = store.Records.Write (recordOf (UserOwner alice) "r/1") (Some 1) "w"
                Expect.equal (expect replaced) (RecordWrite.Written 2) "replaced"
            }

        testCaseAsync "a record survives a reopen" <|
            async {
                let files = freshFiles "record-reopen"
                let keyStore = KeyStore.inMemory ()
                let! store = openDurable files keyStore
                let! _ = store.Records.Write aliceRecord None "kept"
                let! reopened = openDurable files keyStore
                let! read = reopened.Records.Read aliceRecord
                Expect.equal (expect read |> Option.map (fun r -> r.Value)) (Some "kept") "read after the restart"
            }

        testCaseAsync "a record's value is ciphertext on disk" <|
            async {
                let files = freshFiles "record-atrest"
                let! store = openDurable files (KeyStore.inMemory ())
                let! _ = store.Records.Write aliceRecord None "plain-record-value"
                Expect.isFalse ((Fs.readText files.Records).Contains "plain-record-value") "no plaintext at rest"
            }

        testCaseAsync "records are kept in their own file, apart from the secrets" <|
            async {
                let files = freshFiles "record-ownfile"
                let! store = openDurable files (KeyStore.inMemory ())
                let! _ = store.Records.Write aliceRecord None "v"
                Expect.isFalse (Fs.exists files.Secrets) "a record write leaves the secrets file alone"
            }

        testCaseAsync "a ciphertext moved onto another owner's record does not open" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! iv, ct = cipher.Encrypt (SecretsCipher.recordAadFor aliceRecord) "v"
                let bobs = recordOf (UserOwner bob) "repo-trust/v1/github/42"
                let! moved = cipher.Decrypt (SecretsCipher.recordAadFor bobs) iv ct
                Expect.isError moved "bob's AAD does not open alice's ciphertext"
            }

        testCaseAsync "a records file a newer build wrote refuses every records call" <|
            async {
                let files = freshFiles "record-newer"
                newerRecordsFile files
                let! store = openDurable files (KeyStore.inMemory ())
                let! read = store.Records.Read aliceRecord
                Expect.isError read "refused"
            }

        testCaseAsync "a records file a newer build wrote leaves the secrets usable" <|
            async {
                let files = freshFiles "record-newer-secrets"
                newerRecordsFile files
                let! store = openDurable files (KeyStore.inMemory ())
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Secrets.Set id "hunter2"
                let! resolved = store.Secrets.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "the credentials open without the records"
            }

        testCaseAsync "a refused records file is not written over" <|
            async {
                let files = freshFiles "record-newer-kept"
                newerRecordsFile files
                let before = Fs.readText files.Records
                let! store = openDurable files (KeyStore.inMemory ())
                let! _ = store.Records.Write aliceRecord None "v"
                Expect.equal (Fs.readText files.Records) before "the newer build's file is as it was"
            }

        testCaseAsync "a key store with no key over a records file fails rather than minting one" <|
            async {
                // A record can be written before any secret, so the records file alone has to
                // stop a locked keychain from being read as a first run.
                let files = freshFiles "record-nokey"
                let! store = openDurable files (KeyStore.inMemory ())
                let! _ = store.Records.Write aliceRecord None "v"
                let! reopened = SecretStore.openStore (Some files) (KeyStore.inMemory ())
                match reopened with
                | Error (SecretStore.KeyStoreFailure _) -> ()
                | other -> failwithf "expected KeyStoreFailure, got %A" (other |> Result.map ignore)
            }
    ]

let private recordWireTests =
    testList "record wire codecs" [
        testCase "a stale outcome with no record round-trips" <| fun () ->
            let encoded = ControlWire.toString ControlWire.recordWrite (RecordWrite.Stale None)
            Expect.equal (ControlWire.fromString ControlWire.recordWrite encoded) (Ok (RecordWrite.Stale None)) "round trip"

        testCase "a write that expects no record round-trips" <| fun () ->
            let request : ControlWire.WriteRecordRequest =
                { Target = aliceRecord; Expected = None; Value = "{\"a\":1}" }
            let encoded = ControlWire.toString ControlWire.writeRecordRequest request
            Expect.equal (ControlWire.fromString ControlWire.writeRecordRequest encoded) (Ok request) "round trip"

        testCase "a read that found nothing round-trips" <| fun () ->
            let response : ControlWire.ReadRecordResponse = { Record = None }
            let encoded = ControlWire.toString ControlWire.readRecordResponse response
            Expect.equal (ControlWire.fromString ControlWire.readRecordResponse encoded) (Ok response) "round trip"

        testCase "the deployment's record round-trips as its owner" <| fun () ->
            let request : ControlWire.ReadRecordRequest = { Target = recordOf LocalOwner "a/b" }
            let encoded = ControlWire.toString ControlWire.readRecordRequest request
            Expect.equal (ControlWire.fromString ControlWire.readRecordRequest encoded) (Ok request) "round trip"
    ]

/// A control server whose records handlers sit over `store`, with session A's launch signed
/// in to by alice, and session B's by nobody.
let private recordsServer (store: SecretStore.SecretStore) =
    startControlServerWith
        [ "secret-a", caller sessionA (Set.singleton alice)
          "secret-b", caller sessionB Set.empty ]
        None
        (Some (ProcessManager.recordsApiFor ignore store.Records))
        ignore

let private recordRouteTests =
    testList "records control routes" [
        testCaseAsync "a session reads back a record it wrote for a user signed in to it" <|
            async {
                let! store = openEphemeral ()
                let! _, url = recordsServer store
                let records = ControlClient.records (ControlEndpoint.Address url) "secret-a"
                let! _ = records.Write aliceRecord None "trusted"
                let! read = records.Read aliceRecord
                Expect.equal (expect read |> Option.map (fun r -> r.Value)) (Some "trusted") "read back over the wire"
            }

        testCaseAsync "a stale write answers stale over the wire" <|
            async {
                let! store = storeWithRecord "first"
                let! _, url = recordsServer store
                let records = ControlClient.records (ControlEndpoint.Address url) "secret-a"
                let! written = records.Write aliceRecord None "second"
                Expect.equal (expect written) (RecordWrite.Stale (Some 1)) "stale"
            }

        testCaseAsync "a session nobody signed in to cannot read a user's record" <|
            async {
                let! store = storeWithRecord "alice's"
                let! _, url = recordsServer store
                let records = ControlClient.records (ControlEndpoint.Address url) "secret-b"
                let! read = records.Read aliceRecord
                Expect.isError read "refused"
            }
    ]

let tests =
    testList "Secrets" [
        secretsModeTests
        constructorTests
        policyTests
        connectionPolicyTests
        envelopeTests
        wireTests
        cipherTests
        storeTests
        resolutionTests
        familyTests
        auditTests
        recordPolicyTests
        recordStoreTests
        recordWireTests
        Tag.needs "OS keyring" [ Tag.Keyring ] (fun () -> keyringTests)
        Tag.needs "helper key store" [ Tag.Ports ] (fun () -> execTests)
        Tag.needs "Secrets control routes" [ Tag.Ports ] (fun () -> routeTests)
        Tag.needs "Records control routes" [ Tag.Ports ] (fun () -> recordRouteTests)
    ]
