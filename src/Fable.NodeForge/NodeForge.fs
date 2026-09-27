module Fable.NodeForge

// Fable bindings to `node-forge`: enough of its PKI to mint a certificate authority and the
// leaf certificates it signs, and nothing more. Node's `crypto` parses X.509 and cannot build
// it, and forge is already in the tree (sandbox-runtime mints its own CA with it), so a second
// X.509 library would be a second answer to one question.
//
// Key generation goes through forge too: in Node it hands `generateKeyPair` to the native
// `crypto.generateKeyPairSync` when it can, so an RSA key costs milliseconds, not the seconds
// forge's pure-JavaScript path would.
//
// An extension is a bag whose members depend on its `name`, which is how forge reads it. It is
// declared as ONE options interface with every member this binding sets, each optional, built
// with `jsOptions` at the call site — the typed-options escape, not an untyped `createObj`.

open System
open Fable.Core

/// An RSA public key, opaque.
[<AllowNullLiteral>]
type PublicKey = interface end

/// An RSA private key, opaque. Only ever serialized by `Pki.privateKeyToPem`.
[<AllowNullLiteral>]
type PrivateKey = interface end

[<AllowNullLiteral>]
type KeyPair =
    abstract publicKey : PublicKey
    abstract privateKey : PrivateKey

/// A message digest, as `sign` takes one — made fresh per signature.
[<AllowNullLiteral>]
type MessageDigest = interface end

/// One relative distinguished name: `commonName` and its value.
[<AllowNullLiteral>]
type Attribute =
    abstract name : string with get, set
    abstract value : string with get, set

/// One subjectAltName entry. `type` is the GeneralName tag: 2 is a DNS name.
[<AllowNullLiteral>]
type AltName =
    abstract ``type`` : int with get, set
    abstract value : string with get, set

/// One X.509v3 extension, as forge reads it: `name` says which, and the members that name
/// reads say what it asserts. Every member is optional; a member forge does not read for that
/// name is ignored by it.
[<AllowNullLiteral>]
type Extension =
    abstract name : string with get, set
    abstract critical : bool with get, set
    /// basicConstraints
    abstract cA : bool with get, set
    /// keyUsage
    abstract keyCertSign : bool with get, set
    abstract cRLSign : bool with get, set
    abstract digitalSignature : bool with get, set
    abstract keyEncipherment : bool with get, set
    /// extKeyUsage
    abstract serverAuth : bool with get, set
    /// subjectAltName
    abstract altNames : AltName array with get, set

[<AllowNullLiteral>]
type Validity =
    abstract notBefore : DateTime with get, set
    abstract notAfter : DateTime with get, set

/// A certificate being built: set its key, serial, validity, names and extensions, then sign.
[<AllowNullLiteral>]
type Certificate =
    abstract publicKey : PublicKey with get, set
    /// Hex. Positive, which forge does not enforce: a leading byte above 0x7F reads as a
    /// negative serial to a strict parser.
    abstract serialNumber : string with get, set
    abstract validity : Validity
    abstract subject : CertificateName
    abstract setSubject : attributes: Attribute array -> unit
    abstract setIssuer : attributes: Attribute array -> unit
    abstract setExtensions : extensions: Extension array -> unit
    abstract sign : key: PrivateKey * digest: MessageDigest -> unit

/// A certificate's subject or issuer, read back — what a leaf copies as its issuer.
and [<AllowNullLiteral>] CertificateName =
    abstract attributes : Attribute array

[<AllowNullLiteral>]
type Rsa =
    /// Synchronous; native in Node.
    abstract generateKeyPair : bits: int -> KeyPair

[<AllowNullLiteral>]
type Pki =
    abstract rsa : Rsa
    abstract createCertificate : unit -> Certificate
    abstract certificateToPem : certificate: Certificate -> string
    abstract privateKeyToPem : key: PrivateKey -> string

[<AllowNullLiteral>]
type DigestFactory =
    abstract create : unit -> MessageDigest

[<AllowNullLiteral>]
type Digests =
    abstract sha256 : DigestFactory

[<AllowNullLiteral>]
type Forge =
    abstract pki : Pki
    abstract md : Digests

[<ImportDefault("node-forge")>]
let forge : Forge = jsNative
