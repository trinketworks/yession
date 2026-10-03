module Yession.Tests.CredentialProxy

// The credential proxy: a sandbox's tools hold a stand-in, and the provider's credential is
// swapped in here on the way to the provider's host. The pure half (what a CONNECT names, what
// goes up and what comes down) runs in the cheap tier. The [Ports] half drives a real client
// the way a sandbox's does — CONNECT over the proxy's UNIX socket, TLS trusting only the
// bundle it was handed, one request — at a recording stand-in for the provider, because the
// thing under test is what arrives at the far end, and only a real handshake gets there.

open System
open Fable.Core.JsInterop
open Fable.NodeExtras
open Fable.Pyxpecto
open Node.Buffer
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Terminals
open Yession.Host
open Yession.Host.CredentialProxy
open Yession.Host.Interop
open Yession.Tests.Support

let private ada = Principal.User (UserId.create "ada" |> expect)
let private terminal = TerminalId.create "term-a" |> expect
/// The sandbox the TCP door cases admit.
let private admitted = SandboxRef.parse "octo/hello:dev" |> expect

let private route : CredentialRoute =
    { Provider = "example"
      Hosts = [ "api.example.test"; "uploads.example.test" ] }

/// The variables a declaration lends `route`'s token in, for the cases that lend one.
let private lentInto = [ "EXAMPLE_TOKEN"; "EXAMPLE_API_TOKEN" ]

/// A second provider, for the one invariant that needs two.
let private other : CredentialRoute =
    { Provider = "other"; Hosts = [ "api.other.test" ] }

let private headers (pairs: (string * string) list) : (string * HeaderValue)[] =
    pairs |> List.map (fun (name, value) -> name, HeaderValue.Single value) |> List.toArray

let private names (pairs: (string * HeaderValue)[]) = pairs |> Array.map fst |> Array.toList

// --- cheap: what a CONNECT names, and what is carried ---------------------------------------

let private carryTests =
    testList "what the proxy carries" [

        testCase "a CONNECT target is a host and a port" <| fun () ->
            Expect.equal (authority "api.github.com:443") (Some ("api.github.com", 443)) "a name"
            Expect.equal (authority "[::1]:8443") (Some ("::1", 8443)) "a bracketed v6 address"

        testCase "anything else is not a CONNECT target" <| fun () ->
            for target in [ "api.github.com"; ":443"; "api.github.com:"; "api.github.com:0"; "api.github.com:https"; "h:70000" ] do
                Expect.isNone (authority target) (sprintf "%s is not a target" target)

        testCase "a route is found by any host it declares, however it is cased" <| fun () ->
            Expect.equal (routeFor [ other; route ] "UPLOADS.example.test") (Some route) "DNS names are not case-sensitive"

        testCase "a host no route declares has no route" <| fun () ->
            Expect.isNone (routeFor [ route; other ] "example.test") "the parent domain is not declared"

        // What a client's proxy URL becomes: `Basic` over `user:password`, and the password
        // is the capability. Anything else presents none.
        testCase "the capability a CONNECT presents is the password its proxy URL carried" <| fun () ->
            let basic (credentials: string) = "Basic " + Convert.ToBase64String (Text.Encoding.UTF8.GetBytes credentials)
            Expect.equal (presentedCapability (basic "yession:c4p")) (Some "c4p") "the password"
            Expect.equal (presentedCapability (basic ":c4p")) (Some "c4p") "whatever the user was"
            for other in [ basic "yession:"; basic "no-colon"; "Bearer c4p"; "Basic !!!"; "" ] do
                Expect.isNone (presentedCapability other) (sprintf "%s presents none" other)

        // OpenSSL finds a certificate in a directory store only under this name; the value is
        // what `openssl x509 -noout -subject_hash` answers for a certificate with this subject.
        testCase "the authority is named as OpenSSL looks it up in a directory" <| fun () ->
            Expect.equal (subjectHashName "yession credential proxy") "15eb50ae.0" "OpenSSL's subject hash"

        // The word a stand-in replaces, under both schemes a provider's API takes a token in.
        testCase "the credential an authorization header presents is its last word" <| fun () ->
            Expect.equal (presented "token ysn_1") (Some "ysn_1") "gh's scheme"
            Expect.equal (presented "Bearer  ysn_1 ") (Some "ysn_1") "the standard one, spaced carelessly"
            Expect.isNone (presented "  ") "nothing presented"

        testCase "what a client said about its connection to the proxy does not go up" <| fun () ->
            let sent =
                headers
                    [ "host", "api.example.test"
                      "user-agent", "GitHub CLI 2.101.0"
                      "accept", "application/vnd.github+json"
                      "connection", "keep-alive"
                      "proxy-authorization", "Basic cHJveHk="
                      "transfer-encoding", "chunked" ]
            Expect.equal (names (upstreamHeaders sent None)) [ "user-agent"; "accept" ] "only what describes the request"

        // Whatever arrived in `authorization` is replaced, never joined: two would leave the
        // provider to choose, and the one it chose might be the stand-in.
        testCase "the authorization that goes up is the one decided, and it is the only one" <| fun () ->
            let carried = upstreamHeaders (headers [ "authorization", "token ysn_1" ]) (Some "token ghu_real")
            Expect.equal (Array.toList carried) [ "authorization", HeaderValue.Single "token ghu_real" ] "one, decided here"

        testCase "the answer keeps every header but the three that described the upstream hop" <| fun () ->
            let received =
                headers
                    [ "content-type", "application/json"
                      "x-ratelimit-remaining", "4999"
                      "link", "<https://api.example.test/user?page=2>; rel=\"next\""
                      "connection", "keep-alive"
                      "keep-alive", "timeout=5"
                      "transfer-encoding", "chunked" ]
            Expect.equal
                (names (downstreamHeaders received))
                [ "content-type"; "x-ratelimit-remaining"; "link" ]
                "the provider's answer, less the connection it came over"

        // `gh` prints a failed call as `HTTP <status>: <message>` off exactly this field.
        testCase "a refusal is said in the field a provider's client prints" <| fun () ->
            Expect.equal (refusalBody "run it again") """{"message":"run it again"}""" "JSON, with a message"

        testCase "gh's requests reach the swap" <| fun () ->
            Expect.equal (routeFor [ GitHubAccess.route ] "api.github.com") (Some GitHubAccess.route) "the host gh calls"

        // The session's `default` is srt, and it takes a connection by git alone: the API is
        // a container's. Refused rather than given nothing, so a `uses:` of it says why.
        testCase "an srt sandbox is refused the api route, pointed at a container" <| fun () ->
            match forwardApi SrtBackend with
            | WorkSandboxes.CredentialForwarding.Unforwardable reason ->
                Expect.stringContains reason "container" "says where the API is had instead"
            | WorkSandboxes.CredentialForwarding.Forwarded _ -> failwith "srt was given the api route"

        testCase "a container is given the api route, with nothing set up on its behalf" <| fun () ->
            match forwardApi DockerBackend with
            | WorkSandboxes.CredentialForwarding.Forwarded provision ->
                Expect.equal provision WorkSandboxes.Provision.empty "what it reaches the proxy by is what it declared"
            | WorkSandboxes.CredentialForwarding.Unforwardable reason -> failwithf "a container was refused: %s" reason
    ]

// --- cheap: what srt is told ------------------------------------------------------------------

let private interception (socket: string) (hosts: string list) : Interception =
    { Interception.Socket = socket; Interception.Hosts = hosts }

let private srtTests =
    testList "what srt is told" [

        // srt's proxy only hands a host to ours if its config says so, and that config is
        // built from the policy alone.
        testCase "a policy's interception is the mitm proxy srt is configured with" <| fun () ->
            let intercept = interception "/tmp/y.sock" [ "api.example.test" ]
            let tools : Sandboxes.SrtTools =
                { Bwrap = None; Socat = None; Ripgrep = None; Nesting = Sandboxes.StrictNesting; Runtime = [] }
            let config = Sandboxes.SrtSandbox.configFor tools { Support.emptyPolicy with Intercept = Some intercept }
            Expect.equal config.MitmProxy (Some intercept) "the socket and the hosts, as the policy said"

        // There was a case here that the manager's interception only ever widened: srt reads
        // `mitmProxy` from the manager, and one manager served every sandbox of a session.
        // Each sandbox's manager is now its own process, initialized with that sandbox's
        // config and nothing else, so there is no union left to take — what one sandbox
        // intercepts is pinned by the config case above and by the `Srt` tier.
    ]

// --- [Ports]: a real client, through a real proxy -------------------------------------------

/// A stand-in for a provider's API: records the `authorization` of every request that
/// reaches it, and answers with the status a case sets.
type private Upstream =
    { Origin : string
      Authorizations : ResizeArray<string option>
      Answer : int ref
      Close : unit -> Async<unit> }

let private startUpstream () : Async<Upstream> =
    let authorizations = ResizeArray<string option> ()
    let answer = ref 200
    let server =
        createServer (fun req res ->
            authorizations.Add (headerOf req "authorization")
            res.writeHead (answer.Value, [ ResponseHeader.ContentType "application/json" ])
            res.``end`` """{"login":"octo"}""")
    async {
        do! Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont ()) |> ignore)
        return
            { Origin = sprintf "http://127.0.0.1:%d" (serverPort server)
              Authorizations = authorizations
              Answer = answer
              Close = fun () -> Async.FromContinuations (fun (cont, _, _) -> server.close (fun _ -> cont ())) }
    }

/// What one request came back with: the status line's code and everything after the head.
type private Reply = { Status : int; Text : string }

/// Which of the proxy's doors a client comes through: srt's socket, or the TCP port with
/// whatever capability its proxy URL carried.
[<RequireQualifiedAccess>]
type private Door =
    | Socket
    | Port of capability: string option

/// The connection a client dials, and the `proxy-authorization` line its `CONNECT` carries.
let private dial (proxy: Proxy) (door: Door) : Duplex * string =
    match door with
    | Door.Socket -> connectPath proxy.Socket, ""
    | Door.Port capability ->
        connectTcp (proxy.Port, "127.0.0.1"),
        capability
        |> Option.map (fun held ->
            sprintf "Proxy-Authorization: Basic %s\r\n" (Convert.ToBase64String (Text.Encoding.UTF8.GetBytes ("yession:" + held))))
        |> Option.defaultValue ""

/// A `CONNECT` to `target` through `door`, and the status line it was answered with, over
/// the connection that is now the tunnel when that line says so.
let private connectThrough (proxy: Proxy) (door: Door) (target: string) : Async<Result<string * Duplex, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        let mutable settled = false
        let finish outcome =
            if not settled then
                settled <- true
                cont outcome
        let raw, credentials = dial proxy door
        raw.onError (fun error -> finish (Error (StreamError.describe error)))
        raw.writeText (sprintf "CONNECT %s HTTP/1.1\r\nHost: %s\r\n%s\r\n" target target credentials)
        raw.onceData (fun chunk ->
            let head = chunk.toString BufferEncoding.Utf8
            finish (Ok (head.Split("\r\n").[0], raw))))

/// What a sandbox's client does: `CONNECT` through `door`, TLS to `host` trusting the
/// proxy's bundle and nothing else, one `GET`. `Error` carries what refused it — the proxy's
/// answer to the `CONNECT`, or the TLS failure.
let private requestThrough (proxy: Proxy) (door: Door) (host: string) (authorization: string option) : Async<Result<Reply, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        let mutable settled = false
        let finish outcome =
            if not settled then
                settled <- true
                cont outcome
        let raw, credentials = dial proxy door
        raw.onError (fun error -> finish (Error (StreamError.describe error)))
        raw.writeText (sprintf "CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n%s\r\n" host host credentials)
        raw.onceData (fun chunk ->
            let head = chunk.toString BufferEncoding.Utf8
            let statusLine = head.Split("\r\n").[0]
            if not (statusLine.StartsWith "HTTP/1.1 200") then
                finish (Error statusLine)
            else
                let secure =
                    Tls.connect (
                        jsOptions<TlsClientOptions> (fun o ->
                            o.socket <- raw
                            o.servername <- host
                            o.ca <- [| proxy.TrustBundle |])
                    )
                secure.onError (fun error -> finish (Error (StreamError.describe error)))
                let said = Text.StringBuilder ()
                Readables.text secure (fun text -> said.Append text |> ignore)
                secure.onEnd (fun () ->
                    let text = said.ToString ()
                    match text.Split(' ') |> Array.tryItem 1 |> Option.map Int32.TryParse with
                    | Some (true, status) -> finish (Ok { Status = status; Text = text.Substring (max 0 (text.IndexOf "\r\n\r\n")) })
                    | _ -> finish (Error (sprintf "not an HTTP answer: %s" text)))
                let auth = authorization |> Option.map (sprintf "Authorization: %s\r\n") |> Option.defaultValue ""
                secure.writeText (sprintf "GET /user HTTP/1.1\r\nHost: %s\r\n%sConnection: close\r\n\r\n" host auth)))

/// The same, through srt's socket — what every case below but the TCP door's makes.
let private request (proxy: Proxy) (host: string) (authorization: string option) : Async<Result<Reply, string>> =
    requestThrough proxy Door.Socket host authorization

/// A lender whose answers a case controls, and which counts the provider's refusals.
type private Lend =
    { mutable Token : string option
      mutable Refusals : int }

let private lenderOf (lend: Lend) : Lender =
    { Owner = CredentialFor.Person ada
      Resolve = fun () -> async { return lend.Token }
      Refused = fun () -> async { lend.Refusals <- lend.Refusals + 1 } }

/// A proxy for both routes in front of `upstream`, on a socket of its own, for `body`.
let private withProxy (upstream: Upstream) (body: Proxy -> Async<unit>) : Async<unit> =
    async {
        // Canonical, because srt matches a read grant against the path as written (the note in
        // GitIntegration.fs), and the srt case hands a sandbox the trust file in here.
        let dir =
            let made = TestFiles.tempDir "yession-credproxy-"
            Fs.canonical made |> Option.defaultValue made
        let! proxy = CredentialProxy.start [ route; other ] (fun _ -> upstream.Origin) (dir + "/proxy.sock") (dir + "/trust.pem") ignore
        try
            do! body proxy
        finally
            Async.StartImmediate (
                async {
                    do! proxy.Close ()
                    do! upstream.Close ()
                    TestFiles.removeTree dir
                }
            )
    }

let private reply (outcome: Result<Reply, string>) : Reply =
    match outcome with
    | Ok answered -> answered
    | Error refused -> failwithf "the request was refused before it was answered: %s" refused

let private portsTests =
    testList "a real client through the proxy" [

        // The invariant the rest rest on: a client that holds only the stand-in, and trusts
        // only the bundle, reaches the provider — and the credential it never had is what
        // arrives there.
        testCaseAsync "a lent stand-in arrives at the provider as the lender's credential" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let standIn = proxy.Lend route terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        let! answered = request proxy "api.example.test" (Some ("token " + standIn))
                        Expect.equal (reply answered).Status 200 "answered"
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token ghu_real" ] "the lender's credential, under the client's scheme"
                    })
        }

        // A socket path already bound — a process killed before it closed, a pid handed on —
        // is a start that fails in words, never an `error` event that takes the session down.
        testCaseAsync "a socket already taken fails the start in words" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let dir = TestFiles.tempDir "yession-credproxy-"
                        let! second =
                            CredentialProxy.start [ route ] (fun _ -> upstream.Origin) proxy.Socket (dir + "/trust.pem") ignore
                            |> Async.Catch
                        TestFiles.removeTree dir
                        match second with
                        | Choice1Of2 _ -> failwith "a second proxy bound a socket the first holds"
                        | Choice2Of2 refused -> Expect.stringContains refused.Message proxy.Socket "the refusal names the socket"
                    })
        }

        // The TCP door binds every interface, so what keeps it this session's is the
        // capability: a client with none is refused before anything is carried.
        testCaseAsync "the TCP door refuses a CONNECT that carries no live capability" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        for door in [ Door.Port None; Door.Port (Some "not-one-it-minted") ] do
                            match! connectThrough proxy door "api.example.test:443" with
                            | Ok (status, raw) ->
                                raw.destroy ()
                                Expect.stringContains status "407" (sprintf "%A is asked to authenticate" door)
                            | Error e -> failwithf "the door did not answer: %s" e
                    })
        }

        testCaseAsync "an admitted sandbox's stand-in arrives through the TCP door as the lender's credential" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let capability = proxy.Admit DockerBackend admitted |> expect
                        let standIn = proxy.Lend route terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        let! answered = requestThrough proxy (Door.Port (Some capability)) "api.example.test" (Some ("token " + standIn))
                        Expect.equal (reply answered).Status 200 "answered"
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token ghu_real" ] "the lender's credential"
                    })
        }

        // A sandbox with direct egress loses nothing by going through the proxy, and a tool
        // that honours HTTPS_PROXY sends every host this way — so a host no route declares is
        // carried, and carried as it was: nothing read, nothing swapped.
        testCaseAsync "an admitted client's CONNECT to any other host is tunnelled untouched" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let capability = proxy.Admit DockerBackend admitted |> expect
                        let target = upstream.Origin.Replace ("http://", "")
                        match! connectThrough proxy (Door.Port (Some capability)) target with
                        | Error e -> failwithf "the tunnel did not open: %s" e
                        | Ok (status, raw) ->
                            Expect.stringContains status "200" "the tunnel opens"
                            let! said =
                                Async.FromContinuations (fun (cont, _, _) ->
                                    let text = Text.StringBuilder ()
                                    Readables.text raw (fun chunk -> text.Append chunk |> ignore)
                                    raw.onEnd (fun () -> cont (text.ToString ()))
                                    raw.writeText (
                                        sprintf "GET /x HTTP/1.1\r\nHost: %s\r\nAuthorization: token ysn_passing\r\nConnection: close\r\n\r\n" target
                                    ))
                            Expect.stringContains said "octo" "the other host answered, through the tunnel"
                            Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token ysn_passing" ] "and nothing was swapped on the way"
                    })
        }

        // The door tunnels any host, so admitting a sandbox srt confines would hand it a way
        // round its own egress policy.
        testCaseAsync "a sandbox srt confines is never admitted through the TCP door" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        match proxy.Admit SrtBackend admitted with
                        | Ok _ -> failwith "an srt sandbox was admitted"
                        | Error reason -> Expect.stringContains reason "srt" "refused, saying why"
                    })
        }

        testCaseAsync "a dismissed sandbox's capability no longer opens the door" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let capability = proxy.Admit DockerBackend admitted |> expect
                        proxy.Dismiss admitted
                        match! connectThrough proxy (Door.Port (Some capability)) "api.example.test:443" with
                        | Ok (status, raw) ->
                            raw.destroy ()
                            Expect.stringContains status "407" "dismissed is refused"
                        | Error e -> failwithf "the door did not answer: %s" e
                    })
        }

        // What each reference comes to depends on where the sandbox stands: a container sees
        // the proxy's files where they are mounted, and reaches this host by the daemon's name.
        testCaseAsync "a container is given the proxy's URL, and its trust where it is mounted" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let values, provision =
                            provide proxy DockerBackend (Some "host.docker.internal") admitted [ ProxyValue.Https; ProxyValue.CaFile ]
                            |> expect
                        let capability = proxy.Admit DockerBackend admitted |> expect
                        Expect.equal
                            (Map.tryFind ProxyValue.Https values)
                            (Some (sprintf "http://yession:%s@host.docker.internal:%d" capability proxy.Port))
                            "its own URL, with the capability that admits it"
                        Expect.equal (Map.tryFind ProxyValue.CaFile values) (Some "/run/yession/proxy/bundle.pem") "the bundle, where it is mounted"
                        Expect.equal
                            provision.Binds
                            [ { From = proxy.TrustFile; At = "/run/yession/proxy/bundle.pem"; Mode = ResourceMountMode.Read } ]
                            "mounted read-only"
                    })
        }

        testCaseAsync "an unconfined sandbox is given the proxy's own paths" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let values, provision = provide proxy HostBackend (Some "127.0.0.1") admitted [ ProxyValue.CaDir ] |> expect
                        Expect.equal (Map.tryFind ProxyValue.CaDir values) (Some proxy.AuthorityDir) "the directory itself"
                        Expect.isEmpty provision.Binds "nothing to mount"
                        Expect.isTrue
                            (Fs.exists (proxy.AuthorityDir + "/" + subjectHashName "yession credential proxy"))
                            "and the authority is in it, under the name OpenSSL looks for"
                    })
        }

        testCaseAsync "srt is refused the proxy's URL, told what it needs instead" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        match provide proxy SrtBackend (Some "127.0.0.2") admitted [ ProxyValue.Https ] with
                        | Ok _ -> failwith "an srt sandbox was given the TCP door"
                        | Error reason -> Expect.stringContains reason "${proxy.ca-file}" "names what an srt sandbox does need"
                        Expect.isOk (provide proxy SrtBackend (Some "127.0.0.2") admitted [ ProxyValue.CaFile ]) "the trust it may have"
                    })
        }

        // A credential route, not a way out of a sandbox's egress policy.
        testCaseAsync "a host no route declares is refused at the CONNECT" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let! answered = request proxy "example.com" None
                        Expect.equal answered (Error "HTTP/1.1 403 Forbidden") "refused before any TLS"
                    })
        }

        // A stand-in carried to another provider's host goes up as the worthless value it is.
        testCaseAsync "a stand-in is never swapped on another route's host" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let standIn = proxy.Lend route terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        let! _ = request proxy "api.other.test" (Some ("token " + standIn))
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some ("token " + standIn) ] "the stand-in, not the credential"
                    })
        }

        testCaseAsync "a returned stand-in is refused in words, and nothing goes up" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let standIn = proxy.Lend route terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        proxy.Retire terminal
                        let! answered = request proxy "api.example.test" (Some ("token " + standIn))
                        Expect.equal (reply answered).Status 401 "refused"
                        Expect.stringContains (reply answered).Text "has been returned" "saying why"
                        Expect.isEmpty upstream.Authorizations "the provider was never asked"
                    })
        }

        testCaseAsync "a lender with no credential is refused in words" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let standIn = proxy.Lend route terminal (lenderOf { Token = None; Refusals = 0 })
                        let! answered = request proxy "api.example.test" (Some ("token " + standIn))
                        Expect.stringContains (reply answered).Text "has not connected example" "saying what to do"
                        Expect.isEmpty upstream.Authorizations "the provider was never asked"
                    })
        }

        // Nothing lent, nothing decided: the proxy is transparent to a credential it did not mint.
        testCaseAsync "a credential the client brought itself goes up untouched" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let! _ = request proxy "api.example.test" (Some "token mine")
                        Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token mine" ] "as it came"
                    })
        }

        testCaseAsync "a provider refusing the lent credential is told to the lender" <| async {
            let! upstream = startUpstream ()
            upstream.Answer.Value <- 401
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let lend = { Token = Some "ghu_stale"; Refusals = 0 }
                        let standIn = proxy.Lend route terminal (lenderOf lend)
                        let! answered = request proxy "api.example.test" (Some ("token " + standIn))
                        Expect.equal (reply answered).Status 401 "the provider's refusal, relayed"
                        Expect.equal lend.Refusals 1 "and reported"
                    })
        }

        // Whichever variable a tool reads its token from, it holds the same loan — and that
        // loan is what spends.
        testCaseAsync "every variable a block is lent holds one stand-in, and it spends as the lender" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let lent = CredentialProxy.lend proxy route lentInto terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        match lent.Vars |> List.choose snd |> List.distinct with
                        | [ standIn ] ->
                            let! _ = request proxy "api.example.test" (Some ("token " + standIn))
                            Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token ghu_real" ] "the lender's credential"
                        | other -> failwithf "expected one stand-in in %A, got %A" lentInto other
                    })
        }

        // Which bundle a client trusts is bound by whoever wants it (`${proxy.ca-file}`): srt's
        // route to the proxy sets nothing a client reads, so nothing it did not ask for changes
        // what it trusts.
        testCaseAsync "a sandbox routed to the proxy is told nothing it did not ask for" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let provision = CredentialProxy.provision proxy route
                        Expect.isEmpty provision.Env "no variable set on its behalf"
                    })
        }
    ]

// --- [Srt]: a confined client, through srt's proxy into this one ----------------------------------
//
// The seam the Ports suite cannot reach: a sandboxed command's only way out is srt's filtering
// proxy, and what hands a declared host's CONNECT to this proxy is srt's `mitmProxy`, set from
// the policy. Only a real confined curl proves the stand-in survives that route, TLS verifies
// against the bundle it was told to trust, and the provider sees the lender's credential.

let private srtTools () =
    match Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) with
    | Ok tools -> tools
    | Error reason -> failwithf "srt tools: %s" reason

let private confinedTests =
    testList "from an srt sandbox" [

        testCaseAsync "a confined curl spends a lent stand-in, through srt's proxy, as the lender" <| async {
            let! upstream = startUpstream ()
            do!
                withProxy upstream (fun proxy ->
                    async {
                        let workspace =
                            let made = TestFiles.tempDir "yession-credproxy-srt-"
                            Fs.canonical made |> Option.defaultValue made
                        let provision = CredentialProxy.provision proxy route
                        let lent = CredentialProxy.lend proxy route lentInto terminal (lenderOf { Token = Some "ghu_real"; Refusals = 0 })
                        let policy : SandboxPolicy =
                            // The bundle, as `${proxy.ca-file}` provides it: told, and readable.
                            { ReadPaths = workspace :: proxy.TrustFile :: provision.Reads
                              WritePaths = [ workspace ]
                              AllowedDomains = Some provision.Domains
                              Sockets = []
                              Binds = []
                              Volumes = []
                              Realisation = []
                              Env =
                                Sandboxes.mergeEnv (Sandboxes.hostBaseline (Sandboxes.ambientEnv ())) provision.Env
                                |> Map.add "SSL_CERT_FILE" proxy.TrustFile
                                |> Map.add "HOME" workspace
                              WorkingDirectory = Some workspace
                              Filesystem = Confined
                              Derived = Map.empty
                              Intercept = provision.Intercept }
                        match! Sandboxes.SrtSandbox.create (srtTools ()) policy with
                        | Error reason -> failwithf "srt sandbox failed: %s" reason
                        | Ok confined ->
                            let blockEnv = lent.Vars |> List.choose (fun (name, value) -> value |> Option.map (fun v -> name, v)) |> Map.ofList
                            let! run, _, err =
                                runInSandbox
                                    confined
                                    "/bin/sh"
                                    [ "-c"; "curl -sS --fail -H \"Authorization: token $EXAMPLE_TOKEN\" https://api.example.test/user" ]
                                    blockEnv
                                    None
                            Expect.equal run (SandboxExited 0) (sprintf "curl was answered from inside: %s" err)
                            Expect.equal (List.ofSeq upstream.Authorizations) [ Some "token ghu_real" ] "the provider saw the lender's credential"
                            do! confined.Dispose ()
                        TestFiles.removeTree workspace
                    })
        }
    ]

let tests =
    testList "The credential proxy" [
        carryTests
        srtTests
        Tag.needs "The credential proxy, driven by a client" [ Tag.Ports ] (fun () -> portsTests)
        Tag.needs "The credential proxy, from srt" [ Tag.Srt ] (fun () -> confinedTests)
    ]
