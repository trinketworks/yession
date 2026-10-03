module Yession.Host.CredentialProxy

// The credential proxy: how a tool in a sandbox spends a provider's credential WITHOUT holding
// one. What the sandbox is given is a STAND-IN — a value in `GH_TOKEN`, shaped like a token and
// worth nothing anywhere else — and its HTTPS to the provider's hosts is routed here, where TLS
// is terminated with a certificate authority this process minted, the stand-in in
// `authorization` is swapped for the lender's credential, and the request goes on to the real
// host. So `gh`, `curl` and anything else that reads a token from its environment work
// unchanged, and the credential itself never enters the sandbox, its terminal, or the log.
//
// It is the git gateway's idea (`GitGateway.fs`) moved down a layer. The gateway rewrites
// git's URLs to a plain-HTTP route of its own, which only works for a client that can be told
// a different URL; a proxy works for any client that honours `HTTPS_PROXY`.
//
// Nothing here knows a provider. A provider DECLARES a route — its name for sentences, the
// hosts its credential is spent on, the variables its tools read one from — and
// `GitHubAccess.fs` is the first. A stand-in is lent FOR a route and swapped only on that
// route's hosts, so a stand-in carried to another provider's host goes out as the worthless
// value it is, and never as the credential behind it.
//
// It has one door, a TCP port, for a sandbox that reaches the network directly — a container,
// or the unconfined host — and is told `HTTPS_PROXY`. It binds every interface, as the git
// gateway does and for its reason (a container reaches the host at a different address per
// daemon), so it admits a client only by a capability minted for its sandbox (`Admit`), carried
// as the proxy URL's credentials. An admitted client's `CONNECT` to a declared host is answered
// here; to any other host it is TUNNELLED, untouched — a sandbox that reaches the internet
// anyway loses nothing by going this way, and a tool that honours `HTTPS_PROXY` for every host
// has to be carried for every host. That is why an srt sandbox is never admitted: its egress
// IS its policy, and the tunnel would be a way round it. srt is not given the api route at all
// (`forwardApi`); it once had a door of its own, a UNIX socket srt's `mitmProxy` handed
// declared hosts to, and that went with the route.
//
// What this does NOT do: a stand-in is in the environment `env` prints, and every process in a
// sandbox shares one uid — so a stand-in read out of a neighbour's environment spends the
// neighbour's loan. That is the trust boundary a sandbox already is (docs/GAPS.md), the same
// one the git gateway states; what is held is that a stand-in is worth nothing outside this
// proxy, is returned with its block, and is recorded nowhere a durable event could replay it.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.NodeExtras
open Fable.NodeForge
open Node.Api
open Node.Buffer
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What a provider declares about spending its credential over HTTPS. Data, so a second
/// provider is a second declaration and not a second proxy.
type CredentialRoute =
    { /// Who, in a sentence a person reads — "github". Lower case: it is mid-sentence far more
      /// often than it starts one.
      Provider : string
      /// The hosts a stand-in for this route is swapped on — and, across every route, the
      /// only hosts this proxy carries.
      ///
      /// No variables: which ones a block is lent a stand-in in is said where it is wanted —
      /// `${<connection>.token}`, in a resource the operator wrote or a repo's declaration —
      /// and never by the provider on everybody's behalf.
      Hosts : string list }

/// Whose credential answers a stand-in, resolved PER REQUEST — never captured, so a refresh
/// reaches a sandbox already running.
type Lender =
    { /// Whose it is, for the sentences a refusal prints.
      Owner : CredentialFor
      /// The credential now. `None` = the owner has none (any more).
      Resolve : unit -> Async<string option>
      /// The provider refused it: tell whoever tracks the credential's health.
      Refused : unit -> Async<unit> }

// --- what is carried, and how ---------------------------------------------------------------

/// The route that declares `host`, if one does. Hosts compare case-insensitively, as DNS
/// names do.
let routeFor (routes: CredentialRoute list) (host: string) : CredentialRoute option =
    routes
    |> List.tryFind (fun route -> route.Hosts |> List.exists (fun h -> String.Equals (h, host, StringComparison.OrdinalIgnoreCase)))

/// A `CONNECT`'s target, split: `host:port`, or `[v6]:port`. None for anything else.
let authority (target: string) : (string * int) option =
    let at = target.LastIndexOf ':'
    if at <= 0 then None
    else
        let host = target.Substring(0, at).Trim ([| '['; ']' |])
        match Int32.TryParse (target.Substring (at + 1)) with
        | true, port when port > 0 && port < 65536 && host <> "" -> Some (host, port)
        | _ -> None

/// The credential an `authorization` header carries: its last word. That is the token under
/// both schemes a provider's API takes it in — `token <t>` and `Bearer <t>` — and it is the
/// word a stand-in replaces, so the scheme the client chose goes up as it chose it.
let presented (authorization: string) : string option =
    match authorization.Trim().Split ' ' |> Array.filter (fun word -> word <> "") with
    | [||] -> None
    | words -> Some (Array.last words)

/// What a request does NOT carry up. `host`, because Node names the upstream's own from the
/// URL it dials; `authorization`, because the one that goes up is decided here; and the rest
/// because they describe a connection — the client's to this proxy — that is not forwarded.
let private droppedUpstream =
    set
        [ "host"
          "authorization"
          "connection"
          "expect"
          "keep-alive"
          "proxy-authorization"
          "proxy-connection"
          "te"
          "trailer"
          "transfer-encoding"
          "upgrade" ]

/// What the answer does NOT carry back down: the three that describe the upstream connection
/// this process terminated, and that Node decides for itself on the response it writes.
let private droppedDownstream = set [ "connection"; "keep-alive"; "transfer-encoding" ]

/// The headers carried UP: what the client sent, less the set above, then `authorization` as
/// decided — the client's own, the stand-in swapped, or none.
let upstreamHeaders (sent: (string * HeaderValue)[]) (authorization: string option) : (string * HeaderValue)[] =
    Array.append
        (sent |> Array.filter (fun (name, _) -> not (Set.contains name droppedUpstream)))
        (authorization |> Option.map (fun value -> "authorization", HeaderValue.Single value) |> Option.toArray)

/// The headers carried back DOWN: the upstream's, less the three above.
let downstreamHeaders (received: (string * HeaderValue)[]) : (string * HeaderValue)[] =
    received |> Array.filter (fun (name, _) -> not (Set.contains name droppedDownstream))

/// A refusal as a provider's API client prints one: JSON with a `message`, which `gh` shows
/// as `HTTP 401: <message>`.
let refusalBody (message: string) : string =
    Encode.object [ "message", Encode.string message ] |> Encode.toString 0

// --- the certificate authority --------------------------------------------------------------

/// A serial nobody else minted: random, and positive — the leading `01` keeps the first byte
/// below 0x80, where a strict parser would read a negative number.
let private serial () : string = "01" + randomSecret().Replace ("-", "")

/// A validity starting a day ago, so a client whose clock runs behind this one's still
/// accepts it, and running a year — longer than a session process lives.
let private validFor (certificate: Certificate) =
    certificate.validity.notBefore <- DateTime.UtcNow.AddDays -1.0
    certificate.validity.notAfter <- DateTime.UtcNow.AddDays 365.0

let private commonName (name: string) : Attribute array =
    [| jsOptions<Attribute> (fun a ->
           a.name <- "commonName"
           a.value <- name) |]

/// What this authority signs as.
let private authorityName = "yession credential proxy"

/// The authority this process signs with, and the one key every leaf it mints shares. Minted
/// per process and never written anywhere but the trust bundle: it dies with the session, and
/// a sandbox trusts it only because it was told to.
type private Authority =
    { Certificate : Certificate
      Key : PrivateKey
      LeafKeys : KeyPair }

let private mintAuthority () : Authority =
    let pki = forge.pki
    let keys = pki.rsa.generateKeyPair 2048
    let ca = pki.createCertificate ()
    ca.publicKey <- keys.publicKey
    ca.serialNumber <- serial ()
    validFor ca
    let name = commonName authorityName
    ca.setSubject name
    ca.setIssuer name
    ca.setExtensions
        [| jsOptions<Extension> (fun e ->
               e.name <- "basicConstraints"
               e.cA <- true
               e.critical <- true)
           jsOptions<Extension> (fun e ->
               e.name <- "keyUsage"
               e.keyCertSign <- true
               e.cRLSign <- true
               e.critical <- true) |]
    ca.sign (keys.privateKey, forge.md.sha256.create ())
    { Certificate = ca; Key = keys.privateKey; LeafKeys = pki.rsa.generateKeyPair 2048 }

/// A leaf for one host, signed by the authority: the name in its SAN, and good for serving
/// TLS and nothing else.
let private mintLeaf (authority: Authority) (host: string) : SecureContext =
    let pki = forge.pki
    let leaf = pki.createCertificate ()
    leaf.publicKey <- authority.LeafKeys.publicKey
    leaf.serialNumber <- serial ()
    validFor leaf
    leaf.setSubject (commonName host)
    leaf.setIssuer authority.Certificate.subject.attributes
    leaf.setExtensions
        [| jsOptions<Extension> (fun e ->
               e.name <- "basicConstraints"
               e.cA <- false)
           jsOptions<Extension> (fun e ->
               e.name <- "keyUsage"
               e.digitalSignature <- true
               e.keyEncipherment <- true)
           jsOptions<Extension> (fun e ->
               e.name <- "extKeyUsage"
               e.serverAuth <- true)
           jsOptions<Extension> (fun e ->
               e.name <- "subjectAltName"
               e.altNames <-
                   [| jsOptions<AltName> (fun n ->
                          n.``type`` <- 2
                          n.value <- host) |]) |]
    leaf.sign (authority.Key, forge.md.sha256.create ())
    Tls.secureContext (
        jsOptions<SecureContextOptions> (fun o ->
            o.cert <- pki.certificateToPem leaf
            o.key <- pki.privateKeyToPem authority.LeafKeys.privateKey)
    )

/// The name OpenSSL looks a certificate up by in a directory store (`SSL_CERT_DIR`): the
/// subject's hash, then `.0`. OpenSSL's `X509_NAME_hash` is SHA-1 over the canonical subject —
/// each RDN's DER, its value lowercased with runs of spaces made one — and names the file by
/// the first four bytes read little-endian. Go reads every file in the directory whatever its
/// name, so this is the spelling that serves both.
///
/// For a subject of one common name short enough that no length needs a second byte, which is
/// the only subject this authority has; `commonName` below is what it signs as.
let subjectHashName (commonName: string) : string =
    let value =
        Text.Encoding.UTF8.GetBytes (String.Join (" ", commonName.ToLowerInvariant().Split ([| ' ' |], StringSplitOptions.RemoveEmptyEntries)))
    let tagged (tag: byte) (content: byte[]) = Array.concat [ [| tag; byte content.Length |]; content ]
    let attribute = Array.append [| 0x06uy; 0x03uy; 0x55uy; 0x04uy; 0x03uy |] (tagged 0x0Cuy value)
    let canonical = tagged 0x31uy (tagged 0x30uy attribute)
    let hex = canonical |> Array.map (fun b -> b.ToString "x2") |> String.concat ""
    let digest = (Digests.hash "sha1").update(buffer.Buffer.from (hex, BufferEncoding.Hex)).digest BinaryToTextEncoding.Hex
    // The first four bytes, last first.
    String.concat "" [ digest.Substring (6, 2); digest.Substring (4, 2); digest.Substring (2, 2); digest.Substring (0, 2) ] + ".0"

/// What a sandbox is told to trust: this authority, then every root this process trusts.
/// Whole rather than the authority alone, because `SSL_CERT_FILE` and its kin REPLACE a
/// tool's trust store rather than add to it — a bundle of one would verify this proxy and
/// nothing else on the internet.
let private trustBundle (authority: Authority) : string =
    let roots = Array.append (Tls.caCertificates CaStore.Default) (Tls.caCertificates CaStore.System) |> Array.distinct
    String.Join ("\n", Array.append [| forge.pki.certificateToPem authority.Certificate |] roots)

// --- the proxy ------------------------------------------------------------------------------

/// Where a declared host is really reached: over HTTPS, at its own name. A parameter of
/// `start` for the reason every provider endpoint here is — a suite needs somewhere to point
/// it that is not the live provider.
let direct (host: string) : string = "https://" + host

type Proxy =
    { /// The TCP port it listens on, every interface — the door for a sandbox with direct
      /// egress, which a client passes only with a capability from `Admit`.
      Port : int
      /// Admit a sandbox on a backend through the TCP door: the capability its proxy URL
      /// carries, live until `Dismiss`. Asked again, the same sandbox is handed the same one.
      /// An srt sandbox is refused: its egress is its policy, and this door tunnels any host.
      Admit : SandboxBackend -> SandboxRef -> Result<string, string>
      /// The sandbox's capability stops opening the door.
      Dismiss : SandboxRef -> unit
      /// PEM: the authority, and every root this process trusts besides.
      TrustBundle : string
      /// Where `TrustBundle` is written — what a sandbox's `SSL_CERT_FILE` names.
      TrustFile : string
      /// A directory holding this authority ALONE, under the name OpenSSL looks it up by
      /// (`subjectHashName`) — for a store to add it to rather than be replaced by it.
      AuthorityDir : string
      /// Lend a block's requests a route's credential: the stand-in its line exports, live
      /// until the terminal's next loan for that route or `Retire`.
      Lend : CredentialRoute -> TerminalId -> Lender -> string
      /// Every loan on a terminal is returned. A request still carrying one is refused in
      /// words; the last returned stand-in per terminal and route is kept for that sentence.
      Retire : TerminalId -> unit
      Close : unit -> Async<unit> }

/// One block's loan: the route it spends, the terminal it was lent on, who answers it.
type private Loan =
    { Route : CredentialRoute
      Terminal : TerminalId
      Lender : Lender }

/// What a `CONNECT` through the TCP door presents: the capability in its
/// `proxy-authorization`, which a client sends as `Basic` over the proxy URL's `user:password`
/// — the password is the capability, and the user is whatever the URL said. `None` for no
/// header, another scheme, or something that does not decode.
let presentedCapability (proxyAuthorization: string) : string option =
    match proxyAuthorization.Trim().Split ([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries) with
    | [| scheme; encoded |] when scheme.Equals ("Basic", StringComparison.OrdinalIgnoreCase) ->
        try
            let decoded = Text.Encoding.UTF8.GetString (Convert.FromBase64String (encoded.Trim ()))
            match decoded.IndexOf ':' with
            | -1 -> None
            | at -> Some (decoded.Substring (at + 1)) |> Option.filter (fun secret -> secret <> "")
        with _ -> None
    | _ -> None

/// Start the proxy on a TCP port of its own, with its trust bundle written to `trustFile`. `upstream` is where a declared host is reached (`direct`, outside a suite);
/// `report` is where a fault goes that no client can be told about any more — once an
/// answer's head is out there is nothing left to say it on.
let start
    (routes: CredentialRoute list)
    (upstream: string -> string)
    (trustFile: string)
    (report: string -> unit)
    : Async<Proxy> =
    let signer = mintAuthority ()
    let bundle = trustBundle signer
    // Written before anything can be told where it is, and replaced whole: a sandbox that
    // read half a bundle would trust half the internet.
    Fs.writeTextAtomic trustFile bundle
    let authorityDir = path.join (path.dirname trustFile, "proxy-authority")
    Fs.ensureDir authorityDir
    Fs.writeTextAtomic (path.join (authorityDir, subjectHashName authorityName)) (forge.pki.certificateToPem signer.Certificate)
    let mutable contexts : Map<string, SecureContext> = Map.empty
    let contextFor (host: string) =
        match Map.tryFind host contexts with
        | Some context -> context
        | None ->
            let context = mintLeaf signer host
            contexts <- Map.add host context contexts
            context

    /// Live loans, by stand-in.
    let mutable live : Map<string, Loan> = Map.empty
    /// Returned loans, by stand-in — one per terminal and route, so the table stays the size
    /// of the session's terminals and not of its history.
    let mutable returned : Map<string, Loan> = Map.empty

    let retireWhere (returning: Loan -> bool) =
        let back = live |> Map.filter (fun _ loan -> returning loan)
        returned <-
            returned
            |> Map.filter (fun _ held ->
                not (back |> Map.exists (fun _ loan -> loan.Terminal = held.Terminal && loan.Route = held.Route)))
        for KeyValue (standIn, loan) in back do
            returned <- Map.add standIn loan returned
        live <- live |> Map.filter (fun _ loan -> not (returning loan))

    let answer (res: ServerResponse) (status: int) (message: string) =
        res.writeHead (status, [ ResponseHeader.ContentType "application/json"; ResponseHeader.CacheControl "no-store" ])
        res.``end`` (refusalBody message)

    let owner (lender: Lender) = CredentialFor.token lender.Owner

    /// Carry one request up and its answer back, with `authorization` as decided. Answers
    /// the upstream's status, or `None` when no answer came and nothing has been written.
    let forward (host: string) (req: IncomingMessage) (res: ServerResponse) (authorization: string option) : Async<int option> =
        Async.FromContinuations (fun (cont, _, _) ->
            // ONE answer, whatever arrives first: an upstream that errors after its response
            // ended would otherwise resume the caller twice.
            let mutable settled = false
            let finish outcome =
                if not settled then
                    settled <- true
                    cont outcome
            let up =
                httpRequest
                    (upstream host + req.url)
                    req.``method``
                    (upstreamHeaders (req.headerEntries ()) authorization)
                    (fun reply ->
                        res.relayHead (reply.statusCode, downstreamHeaders (reply.headerEntries ()))
                        reply.pipe res
                        reply.onEnd (fun () -> finish (Some reply.statusCode))
                        reply.onError (fun _ ->
                            res.destroy ()
                            finish (Some reply.statusCode)))
            up.onError (fun error ->
                if res.headersSent then
                    res.destroy ()
                    report (sprintf "the credential proxy lost %s mid-answer: %s" host (StreamError.describe error))
                    finish (Some 0)
                else
                    finish None)
            req.onError (fun _ -> up.destroy ())
            req.pipe up)

    /// One decrypted request to `host`, on `route`.
    let handle (host: string) (route: CredentialRoute) (req: IncomingMessage) (res: ServerResponse) =
        let authorization = headerOf req "authorization"
        let unreachable () =
            answer res 502 (sprintf "%s could not be reached from this session" host)
        let carry (authorization: string option) (after: int -> Async<unit>) =
            async {
                match! forward host req res authorization with
                | None -> unreachable ()
                | Some status -> do! after status
            }
        let run (work: Async<unit>) =
            Async.StartImmediate (
                async {
                    try
                        do! work
                    with e ->
                        if not res.headersSent then answer res 502 (sprintf "the credential proxy failed: %s" e.Message)
                        else report (sprintf "the credential proxy failed after answering a request to %s: %s" host e.Message)
                }
            )
        // A stand-in counts only on the route it was lent for: carried anywhere else it is the
        // worthless value it looks like, and goes up as one.
        let onRoute (loan: Loan) = loan.Route = route
        let standIn = authorization |> Option.bind presented
        let lent = standIn |> Option.bind (fun s -> Map.tryFind s live) |> Option.filter onRoute
        let given = standIn |> Option.bind (fun s -> Map.tryFind s returned) |> Option.filter onRoute
        match lent, given with
        | Some loan, _ ->
            run (
                async {
                    match! loan.Lender.Resolve () with
                    | None ->
                        answer
                            res
                            401
                            (sprintf
                                "%s has not connected %s — connect it on the settings panel and run the command again"
                                (owner loan.Lender)
                                route.Provider)
                    | Some token ->
                        let swapped = authorization |> Option.map (fun header -> header.Replace (standIn.Value, token))
                        do! carry swapped (fun status -> if status = 401 then loan.Lender.Refused () else async.Zero ())
                }
            )
        | None, Some loan ->
            answer
                res
                401
                (sprintf
                    "the %s credential lent to %s for that command has been returned — run it as its own command"
                    route.Provider
                    (owner loan.Lender))
        // Nothing lent here: the client's own credential, or none, carried as it came.
        | None, None -> run (carry authorization (fun _ -> async.Zero ()))

    let refuseConnect (client: Duplex) (status: string) (extra: string) (message: string) =
        client.writeText (
            sprintf "HTTP/1.1 %s\r\ncontent-type: text/plain\r\n%sconnection: close\r\n\r\n%s\n" status extra message
        )
        client.finish ()

    /// A declared host's `CONNECT`: terminated here, and each request inside it answered on
    /// its route.
    let intercept (host: string) (route: CredentialRoute) (client: Duplex) (head: Buffer) =
        client.writeText "HTTP/1.1 200 Connection Established\r\n\r\n"
        if head.length > 0 then client.unshift head
        let tls = Tls.terminate client (contextFor host)
        tls.onError ignore
        // One server per tunnel, so the host a request is answered for is the one the
        // CONNECT named — never the `Host` header the client put inside it.
        (createServer (handle host route)).serve tls

    /// Any other host's `CONNECT`, from a client that reaches the internet anyway: the bytes
    /// both ways, read by nobody. Answered only once the upstream is open, so a host that
    /// cannot be reached is a refusal the client can print rather than a tunnel that dies.
    let tunnel (host: string) (port: int) (client: Duplex) (head: Buffer) =
        let upstream = connectTcp (port, host)
        let mutable opened = false
        upstream.onError (fun error ->
            if opened then client.destroy ()
            else refuseConnect client "502 Bad Gateway" "" (sprintf "%s:%d could not be reached: %s" host port (StreamError.describe error)))
        client.onError (fun _ -> upstream.destroy ())
        upstream.onceConnect (fun () ->
            opened <- true
            client.writeText "HTTP/1.1 200 Connection Established\r\n\r\n"
            if head.length > 0 then upstream.sink.writeBytes head |> ignore
            upstream.pipe client.sink
            client.pipe upstream.sink)

    /// Capabilities admitted through the TCP door, by sandbox.
    let mutable admitted : Map<SandboxRef, string> = Map.empty

    let onPortConnect (request: ConnectRequest) (client: Duplex) (head: Buffer) =
        client.onError ignore
        let capability =
            request.headerEntries ()
            |> Array.tryPick (fun (name, value) ->
                match value with
                | HeaderValue.Single text when name = "proxy-authorization" -> presentedCapability text
                | _ -> None)
        match capability with
        | Some held when admitted |> Map.exists (fun _ capability -> capability = held) ->
            match authority request.url with
            | None -> refuseConnect client "400 Bad Request" "" (sprintf "not a CONNECT target: %s" request.url)
            | Some (host, port) ->
                match routeFor routes host with
                | Some route -> intercept host route client head
                | None -> tunnel host port client head
        | _ ->
            refuseConnect
                client
                "407 Proxy Authentication Required"
                "proxy-authenticate: Basic realm=\"yession\"\r\n"
                "this proxy admits a sandbox by the capability in the proxy URL it was given, and this request carried none that is live"

    let refuseRequest = fun _ res -> answer res 405 "this proxy carries HTTPS, through CONNECT, and nothing else"
    let onPort = createServer refuseRequest
    onPort.onConnect (Func<_, _, _, _> onPortConnect)
    let listening (server: HttpServer) (where: string) (listen: (unit -> unit) -> unit) =
        Async.FromContinuations (fun (cont, fail, _) ->
            server.onceError (fun error ->
                fail (exn (sprintf "credential proxy cannot listen on %s: %s" where (StreamError.describe error))))
            listen cont)
    async {
        do! listening onPort "a TCP port" (fun cont -> onPort.listen (0, "0.0.0.0", fun () -> cont ()) |> ignore)
        let close (server: HttpServer) = Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ()))
        return
            { Port = serverPort onPort
              Admit =
                fun backend sandbox ->
                    match backend, Map.tryFind sandbox admitted with
                    | SrtBackend, _ ->
                        Error (
                            sprintf
                                "sandbox '%s' is confined by srt, whose egress is its policy — it reaches this proxy through srt's own, for declared hosts only, and the TCP door would tunnel it anywhere"
                                (SandboxRef.render sandbox)
                        )
                    | _, Some capability -> Ok capability
                    | _, None ->
                        let capability = randomSecret().Replace ("-", "")
                        admitted <- Map.add sandbox capability admitted
                        Ok capability
              Dismiss = fun sandbox -> admitted <- Map.remove sandbox admitted
              TrustBundle = bundle
              TrustFile = trustFile
              AuthorityDir = authorityDir
              Lend =
                fun route terminal lender ->
                    retireWhere (fun loan -> loan.Terminal = terminal && loan.Route = route)
                    let standIn = "ysn_" + randomSecret().Replace ("-", "")
                    live <- Map.add standIn { Route = route; Terminal = terminal; Lender = lender } live
                    standIn
              Retire = fun terminal -> retireWhere (fun loan -> loan.Terminal = terminal)
              Close =
                fun () ->
                    async {
                        do! close onPort
                    } }
    }

// --- what a sandbox and a block are given ------------------------------------------------------

/// What a connection's `api` route forwards into a sandbox on `backend`. Docker and the
/// unconfined host reach the internet directly, and reach this proxy only through the TCP door
/// by a URL their declaration asks for (`${proxy.https}`, `Admit`) — so there is nothing to
/// set up for them here, and what they are lent is what they declared.
///
/// srt is refused. Its sandbox is the session's `default`: a minimal place to check a repo out
/// and commit, which takes GitHub by `git` through the gateway and nothing more. The API is a
/// work sandbox's job — a container, where `gh` and its kind work. Under srt they did not: on
/// macOS a Go client verifies TLS through the Security framework, which srt blocks, so `gh`
/// failed every request; and Node there cannot resolve `localhost`, the name in srt's own
/// proxy URL. Offering the route anyway made a sandbox that looked able to reach the API and
/// was not. Refused in words, so a `uses:` says why at the start, and a `wants:` is had by its
/// other routes and goes without this one, as a want does.
let forwardApi (backend: SandboxBackend) : WorkSandboxes.CredentialForwarding =
    match backend with
    | SrtBackend ->
        WorkSandboxes.CredentialForwarding.Unforwardable
            "an srt sandbox takes a connection by git alone — the api route (gh, the credential proxy) is for a container sandbox, which reaches the proxy by '${proxy.https}'"
    | HostBackend
    | DockerBackend -> WorkSandboxes.CredentialForwarding.Forwarded WorkSandboxes.Provision.empty

/// What one block is lent on `route`: a fresh stand-in, in each of `variables` — the same
/// stand-in in each, so the block's requests are one loan whichever variable a tool happened
/// to read. Nothing at all for no variables: a loan nobody could read is one not worth minting.
let lend (proxy: Proxy) (route: CredentialRoute) (variables: string list) (terminal: TerminalId) (lender: Lender) : BlockEnv =
    match List.distinct variables with
    | [] -> BlockEnv.none
    | variables ->
        let standIn = proxy.Lend route terminal lender
        { BlockEnv.GitConfig = None
          BlockEnv.Vars = variables |> List.map (fun name -> name, Some standIn) }

/// Where a container sees what the proxy gives it to trust: the bundle and the authority's
/// directory, mounted read-only. Fixed rather than following the host's paths, because a
/// container's filesystem is its image's and the session's data directory means nothing there.
let private inContainer = "/run/yession/proxy"

/// What the proxy provides a sandbox on `backend` that asked for `asked`, reaching this host as
/// `hostAddress` — each value as THIS sandbox sees it, and what it needs to use them. Refused,
/// saying why, for an srt sandbox: it is not given the api route (`forwardApi`), so nothing
/// should ask on its behalf, and one that did is told so rather than handed paths to a proxy
/// it has no way to.
let provide
    (proxy: Proxy)
    (backend: SandboxBackend)
    (hostAddress: string option)
    (sandbox: SandboxRef)
    (asked: ProxyValue list)
    : Result<Map<ProxyValue, string> * WorkSandboxes.Provision, string> =
    let seen (hostPath: string) (containerPath: string) : string * WorkSandboxes.Provision =
        match backend with
        | DockerBackend ->
            containerPath,
            { WorkSandboxes.Provision.empty with
                Binds = [ { From = hostPath; At = containerPath; Mode = ResourceMountMode.Read } ] }
        | HostBackend
        | SrtBackend -> hostPath, WorkSandboxes.Provision.empty
    let one (value: ProxyValue) : Result<string * WorkSandboxes.Provision, string> =
        match value with
        | ProxyValue.CaFile -> Ok (seen proxy.TrustFile (inContainer + "/bundle.pem"))
        | ProxyValue.CaDir -> Ok (seen proxy.AuthorityDir (inContainer + "/authority"))
        | ProxyValue.Https ->
            match hostAddress with
            | None ->
                Error (sprintf "sandbox '%s' has no way to reach this host, so no proxy URL would get anywhere" (SandboxRef.render sandbox))
            | Some host ->
                proxy.Admit backend sandbox
                |> Result.map (fun capability ->
                    sprintf "http://yession:%s@%s:%d" capability host proxy.Port, WorkSandboxes.Provision.empty)
    match backend with
    | SrtBackend ->
        Error (
            sprintf
                "sandbox '%s' is confined by srt, which takes a connection by git alone — the credential proxy is for a container sandbox"
                (SandboxRef.render sandbox)
        )
    | HostBackend
    | DockerBackend ->
        asked
        |> List.distinct
        |> List.fold
            (fun acc value ->
                acc
                |> Result.bind (fun (values, provision) ->
                    one value
                    |> Result.map (fun (text, given) -> Map.add value text values, WorkSandboxes.Provision.merge provision given)))
            (Ok (Map.empty, WorkSandboxes.Provision.empty))

/// The session's proxy, as a sandbox registry asks it: `provide` for each sandbox's backend and
/// the name that sandbox reaches this host by, and a sandbox forgotten through the TCP door
/// when it leaves.
let provider
    (proxy: Proxy)
    (backendOf: SandboxRef -> SandboxBackend)
    (hostAddressOf: SandboxRef -> string option)
    : WorkSandboxes.ProxyProvider =
    { Provide = fun sandbox asked -> provide proxy (backendOf sandbox) (hostAddressOf sandbox) sandbox asked
      Release = proxy.Dismiss }
