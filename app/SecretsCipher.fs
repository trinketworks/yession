module Yession.Host.SecretsCipher

// AES-256-GCM over Node's built-in WebCrypto (Plan 06). The key is imported with
// `extractable = false` — after import no code path can serialize it (`exportKey`
// rejects; pinned by test) — the same discipline ManagerOidc applies to the OIDC
// signing key. WebCrypto alone cannot PERSIST such a key (it has no OS keystore;
// non-extractability forbids serialization by design), which is exactly why the raw
// key bytes live in the OS credential manager (KeyStore) and are re-imported each
// start. Every encryption takes an AAD string binding the ciphertext to its owning
// entry's identity, so a ciphertext transplanted onto another entry fails GCM
// authentication.
//
// The platform is reached through `Fable.NodeExtras` rather than an `[<Emit>]` apiece: what
// is interesting here is the SEQUENCE — mint an IV, encrypt under it, encode both — and a
// sequence written inside an emit body is a program no compiler and no cheap test can read.

open Fable.Core
open Fable.NodeExtras
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Access

/// Every string crossing this boundary is utf8 going in and coming out; only the IV and the
/// ciphertext travel as base64url, because they are bytes that have to survive JSON.
let private utf8 = BufferEncoding.Utf8

/// An imported, non-extractable AES-GCM key plus the operations it supports.
type Cipher =
    { /// aad -> plaintext -> (ivB64u, ciphertextB64u); fresh random 96-bit IV per call.
      Encrypt : string -> string -> Async<string * string>
      /// aad -> ivB64u -> ciphertextB64u -> plaintext. Error on GCM authentication
      /// failure: tampered ciphertext, transplanted entry, or the wrong KEK.
      Decrypt : string -> string -> string -> Async<Result<string, string>>
      /// The imported CryptoKey — the non-extractability pin surface for tests.
      Key : CryptoKey }

/// 32 random bytes, base64url — a fresh KEK (created once per store on first run, or
/// per boot for the ephemeral store).
let generateKek () : string =
    (bufferOf ((webcrypto ()).getRandomValues (JS.Constructors.Uint8Array.Create 32))).toString base64url

/// Import a raw KEK as the NON-EXTRACTABLE AES-GCM key every operation below runs on. The
/// `false` is that extractability, and it is the discipline the whole secrets store rests on.
let private importRaw (kekB64u: string) : JS.Promise<CryptoKey> =
    (webcrypto ())
        .subtle.importKey (
            KeyFormat.Raw,
            buffer.Buffer.from (kekB64u, base64url),
            AesGcm.algorithm,
            false,
            [| KeyUsage.Encrypt; KeyUsage.Decrypt |])

/// One encryption: a fresh 96-bit IV, the ciphertext under it, and both as base64url. The IV
/// is minted HERE rather than taken as an argument because a repeat under one key is what GCM
/// has no defence against — a caller that could supply one is the caller that eventually does.
let private encrypt (key: CryptoKey) (aad: string) (plaintext: string) : Async<string * string> =
    async {
        let iv = (webcrypto ()).getRandomValues (JS.Constructors.Uint8Array.Create 12)

        let! ciphertext =
            (webcrypto ())
                .subtle.encrypt (
                    AesGcm.parameters iv (buffer.Buffer.from (aad, utf8)),
                    key,
                    buffer.Buffer.from (plaintext, utf8))
            |> Interop.awaitPromise

        return (bufferOf iv).toString base64url, (buffer.Buffer.from ciphertext).toString base64url
    }

/// The other direction, under the SAME AAD — which is the whole binding: the platform REJECTS
/// rather than answering when the ciphertext, the IV, the AAD or the key is not the one it was
/// sealed with. That rejection is the guarantee; `importKey` below is where it becomes an
/// `Error` rather than an escaping exception.
let private decrypt (key: CryptoKey) (aad: string) (ivB64u: string) (ciphertextB64u: string) : Async<string> =
    async {
        let iv = bytesOf (buffer.Buffer.from (ivB64u, base64url))

        let! plaintext =
            (webcrypto ())
                .subtle.decrypt (
                    AesGcm.parameters iv (buffer.Buffer.from (aad, utf8)),
                    key,
                    buffer.Buffer.from (ciphertextB64u, base64url))
            |> Interop.awaitPromise

        return (buffer.Buffer.from plaintext).toString utf8
    }

/// The AAD binding a ciphertext to the entry it encrypts: version + scope + name.
let aadFor (scope: SecretScope) (name: SecretName) : string =
    sprintf "yession-secret:1:%s:%s" (SecretScope.describe scope) (SecretName.value name)

/// Import a raw KEK as a NON-EXTRACTABLE AES-GCM key and return the cipher over it.
let importKey (kekB64u: string) : Async<Cipher> =
    async {
        let! key = importRaw kekB64u |> Interop.awaitPromise
        return
            { Encrypt = encrypt key
              Decrypt =
                fun aad iv ciphertext ->
                    async {
                        try
                            let! plaintext = decrypt key aad iv ciphertext
                            return Ok plaintext
                        with _ ->
                            return Error "decryption failed (tampered, transplanted, or sealed by a different key)"
                    }
              Key = key }
    }
