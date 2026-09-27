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
open Yession.Host
open Yession.Host.CredentialProxy
open Yession.Host.Interop
open Yession.Tests.Support

let private ada = Principal.User (UserId.create "ada" |> expect)
let private terminal = TerminalId.create "term-a" |> expect

let private route : CredentialRoute =
    { Provider = "example"; Hosts = [ "api.example.test"; "uploads.example.test" ]; Variables = [ "EXAMPLE_TOKEN" ] }

/// A second provider, for the one invariant that needs two.
let private other : CredentialRoute =
    { Provider = "other"; Hosts = [ "api.other.test" ]; Variables = [ "OTHER_TOKEN" ] }

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

        testCase "GitHub's stand-in reaches gh, and gh's requests reach the swap" <| fun () ->
            Expect.isTrue (List.contains "GH_TOKEN" GitHubAccess.route.Variables) "the variable gh reads a token from"
            Expect.equal (routeFor [ GitHubAccess.route ] "api.github.com") (Some GitHubAccess.route) "the host gh calls"
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

/// What a sandbox's client does: `CONNECT` over the proxy's socket, TLS to `host` trusting
/// the proxy's bundle and nothing else, one `GET`. `Error` carries what refused it — the
/// proxy's answer to the `CONNECT`, or the TLS failure.
let private request (proxy: Proxy) (host: string) (authorization: string option) : Async<Result<Reply, string>> =
    Async.FromContinuations (fun (cont, _, _) ->
        let mutable settled = false
        let finish outcome =
            if not settled then
                settled <- true
                cont outcome
        let raw = connectPath proxy.Socket
        raw.onError (fun error -> finish (Error (StreamError.describe error)))
        raw.writeText (sprintf "CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n" host host)
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
        let dir = TestFiles.tempDir "yession-credproxy-"
        let! proxy = CredentialProxy.start [ route; other ] (fun _ -> upstream.Origin) (dir + "/proxy.sock") ignore
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
    ]

let tests =
    testList "The credential proxy" [
        carryTests
        Tag.needs "The credential proxy, driven by a client" [ Tag.Ports ] (fun () -> portsTests)
    ]
