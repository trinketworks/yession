module Yession.Tests.Models

// The model catalogue, end to end below the browser: the provider lookup that produces it,
// and the status reply that carries it.
//
// What is worth pinning here is not "a list comes back". It is the handful of properties
// the picker leans on:
//
//   * a paged catalogue arrives WHOLE, because a lookup that silently stopped at page one
//     would read as "this provider offers nothing else";
//   * the credential decides the dialect, so an OAuth grant and an API key each present
//     themselves the way their provider expects rather than one guessing for both;
//   * a failed lookup says why, and says it all the way to the browser — "nobody has
//     connected an account" and "this provider offers nothing" are different facts and a
//     picker that conflated them would show an empty menu with no way out;
//   * it rides the credential status it is a fact about, so the two cannot disagree;
//   * the door is shut: what this session can run on is for the people in the session.
//
// Ports, because the lookup IS an HTTP conversation. There is no in-memory stand-in for
// paging, headers and a status code, and those are exactly what the cases turn on.

open Fable.Core
open Thoth.Json
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Access
open Yession.Domain.Agent
open Yession.App
open Yession.Host
open Yession.SessionProcess

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

/// A GET, with the cookie header only when there is a cookie — which is what the `cookie ? ..
/// : {}` this used to carry inside a `fetch` macro decided. `IsNullOrEmpty` rather than
/// `= ""` because that ternary was JS truthiness, and an absent cookie reaches here as either.
let private get (url: string) (cookie: string) : Async<TestHttp.Reply> =
    let headers =
        if System.String.IsNullOrEmpty cookie then [] else [ "cookie", cookie ]

    TestHttp.getNoStore headers url

/// Start a server on a free port and answer with `reply`, which sees the request.
let private serving (handler: Interop.IncomingMessage -> Interop.ServerResponse -> unit) =
    async {
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) -> server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return sprintf "http://127.0.0.1:%d" (Interop.serverPort listening), server
    }

let private json (res: Interop.ServerResponse) (status: int) (body: string) =
    res.writeHead (status, JsInterop.createObj [ "content-type", box "application/json" ]) |> ignore
    res.``end`` body

// --- the provider lookup ------------------------------------------------------------------

/// A provider that answers the models endpoint in two pages, and records how each request
/// presented itself — which is what the dialect cases read back.
let private pagedProvider (seen: ResizeArray<string>) =
    fun (req: Interop.IncomingMessage) (res: Interop.ServerResponse) ->
        let auth =
            match Interop.headerOf req "x-api-key", Interop.headerOf req "authorization" with
            | Some key, _ -> "x-api-key:" + key
            | _, Some bearer -> "authorization:" + bearer
            | _ -> "none"
        seen.Add auth
        if (req.url : string).Contains "after_id=" then
            json res 200 """{"data":[{"id":"model-c","display_name":"Model C"}],"has_more":false,"last_id":"model-c"}"""
        else
            json
                res
                200
                """{"data":[{"id":"model-a","display_name":"Model A"},{"id":"model-b","display_name":"Model B"}],"has_more":true,"last_id":"model-b"}"""

let private lookupTests =
    testList "the provider lookup" [

        testCaseAsync "a paged catalogue arrives whole" <|
            async {
                // The failure this exists for is the quiet one: stop at page one and the
                // picker offers a subset nobody can tell is a subset.
                let seen = ResizeArray<string> ()
                let! url, server = serving (pagedProvider seen)
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                server.close ignore
                Expect.equal
                    (models |> expect |> List.map (fun m -> ModelId.value m.Id))
                    [ "model-a"; "model-b"; "model-c" ]
                    "every page's models, in the provider's order"
            }

        testCaseAsync "an api key presents itself as one, and an oauth grant as a bearer" <|
            async {
                // One credential PAIR decides this, so there is nothing to guess from the
                // shape of a secret — and nothing that can send a token the wrong way.
                let seen = ResizeArray<string> ()
                let! url, server = serving (pagedProvider seen)
                let! _ = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-key")
                let! _ = ClaudeConnection.modelsAt url ("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat01-grant")
                server.close ignore
                Expect.isTrue (Seq.contains "x-api-key:sk-ant-key" seen) "the key goes in the key header"
                Expect.isTrue
                    (Seq.contains "authorization:Bearer sk-ant-oat01-grant" seen)
                    "and the grant goes in the bearer header"
            }

        testCaseAsync "a refused lookup answers with what the provider said" <|
            async {
                // The detail is the whole value: "401 invalid x-api-key" is actionable and
                // "could not list models" is not.
                let! url, server = serving (fun _ res -> json res 401 """{"error":{"message":"invalid x-api-key"}}""")
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-wrong")
                server.close ignore
                match models with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure ->
                    Expect.isTrue (failure.Message.Contains "401") "the status is in the reason"
                    Expect.isTrue (failure.Message.Contains "invalid x-api-key") "and so is what the provider said"
                    // And it is marked as a fact about the CREDENTIAL, which is what lets it
                    // travel back to the Manager rather than stopping at the picker's note.
                    Expect.isTrue failure.Refused "a 401 is the provider refusing this credential"
            }

        // The other side of that line. Both of these happen to a credential that is working
        // perfectly, so both must leave it alone: telling somebody to sign in again because
        // an org policy blocked one endpoint, or because the provider had a bad minute,
        // spends their time on the wrong problem.
        testCaseAsync "a lookup that fails for reasons other than the credential leaves it alone" <|
            async {
                let! forbiddenUrl, forbiddenServer = serving (fun _ res -> json res 403 """{"error":{"message":"not permitted"}}""")
                let! forbidden = ClaudeConnection.modelsAt forbiddenUrl ("ANTHROPIC_API_KEY", "sk-ant-fine")
                forbiddenServer.close ignore
                match forbidden with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure -> Expect.isFalse failure.Refused "403 is a policy or a scope, not a dead key"

                let! brokenUrl, brokenServer = serving (fun _ res -> json res 500 """{"error":{"message":"oops"}}""")
                let! broken = ClaudeConnection.modelsAt brokenUrl ("ANTHROPIC_API_KEY", "sk-ant-fine")
                brokenServer.close ignore
                match broken with
                | Ok _ -> failwith "a refusal must not read as a catalogue"
                | Error failure -> Expect.isFalse failure.Refused "a provider having a bad minute is not a verdict"
            }

        // The same rule one step earlier: a row that is not an object at all cannot be read,
        // and reading the rest of the page is still the right answer. The page used to be
        // unboxed, so this row reached the id invariant as `undefined` and was refused there
        // by accident; now the decoder drops it on purpose and says so here.
        testCaseAsync "a row that is not a row at all costs that row, never the catalogue" <|
            async {
                let! url, server =
                    serving (fun _ res ->
                        json res 200 """{"data":[5,"model-b",{"id":"model-a"}],"has_more":false}""")
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                server.close ignore
                match models |> expect with
                | [ only ] -> Expect.equal (ModelId.value only.Id) "model-a" "the usable row survives its neighbours"
                | other -> failwithf "expected one usable model, got %A" other
            }

        testCaseAsync "a row the id invariant refuses costs that row, never the catalogue" <|
            async {
                let! url, server =
                    serving (fun _ res ->
                        json res 200 """{"data":[{"id":"","display_name":"Nameless"},{"id":"model-a"}],"has_more":false}""")
                let! models = ClaudeConnection.modelsAt url ("ANTHROPIC_API_KEY", "sk-ant-test")
                server.close ignore
                match models |> expect with
                | [ only ] ->
                    Expect.equal (ModelId.value only.Id) "model-a" "the usable row survives"
                    Expect.equal only.Name "model-a" "and stands in for its own missing label"
                | other -> failwithf "expected one usable model, got %A" other
            }
    ]

// --- the route --------------------------------------------------------------------------

/// `who=<name>` is an identity; anything else is nobody — the same stub the query route's
/// suite uses, and for the same reason: what is under test is what the ROUTE does with one.
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

/// A broker that refuses everything: what is under test here is the STATUS reply, and no
/// case in this suite drives a write action.
let private stubConnections : ControlClient.SessionConnections =
    let refuse _ = async { return Error "not under test" }
    { Begin = refuse
      Complete = fun _ _ -> refuse ()
      Put = fun _ _ -> refuse ()
      PutGrant = refuse
      Disconnect = refuse
      Reject = fun _ _ -> refuse ()
      Resolve = refuse }

/// The Claude panel over a stub catalogue. `panelFor` is what the read stream pushes, and
/// it is the whole answer to "what can a turn run on here" — so these ask it directly
/// rather than through a route. There is no status route to drive any more: the panel's
/// own door is the stream's, pinned where the stream is.
let private panelFor (list: ListModels) (identity: CookieIdentity) : Async<ClaudePanel> =
    ClaudeConnection.panelFor
        (SessionId.create "sess-models" |> expect)
        (fun _ -> None)
        (fun () -> false)
        list
        identity

let private ada : CookieIdentity =
    { Subject = "ada"
      DisplayName = None
      Attribution = AttributedUser (UserId.create "ada" |> expect) }

let private catalogueTests =
    testList "the catalogue on the panel" [

        testCaseAsync "the catalogue is asked for on the looking party's authority" <|
            async {
                let mutable askedFor : CredentialFor option = None
                let! panel =
                    panelFor
                        (fun actor ->
                            async {
                                askedFor <- Some actor
                                return Ok [ AgentModel.create (ModelId.create "model-a" |> expect) "Model A" ]
                            })
                        ada
                Expect.equal
                    panel.Models
                    (ModelsLoaded [ AgentModel.create (ModelId.create "model-a" |> expect) "Model A" ])
                    "the catalogue is what the provider answered"
                Expect.equal
                    askedFor
                    (Some (CredentialFor.Person (Principal.User (UserId.create "ada" |> expect))))
                    "asked on the credential of whoever is looking"
            }

        testCaseAsync "a lookup that failed says why, rather than answering an empty list" <|
            async {
                // An empty menu with no explanation is the state this whole shape exists to
                // avoid: the remedy is one panel up, and nothing would have pointed at it.
                let! panel = panelFor (fun _ -> async { return Error "no Claude account connected" }) ada
                Expect.equal
                    panel.Models
                    (ModelsUnavailable "no Claude account connected")
                    "the reason is the answer, and an empty list is not"
            }

        testCaseAsync "the credential status and the catalogue are one value" <|
            async {
                // The invariant the whole shape exists for. Two routes with two refresh
                // triggers let the picker keep a refusal naming an account the panel beside
                // it had already shown as connected; one value cannot disagree with itself,
                // which is a stronger promise than one reply.
                let! panel = panelFor (fun _ -> async { return Error "no Claude account connected" }) ada
                Expect.equal panel.Owner (Some "user") "the panel says whose the shared scope is"
                Expect.equal
                    panel.Models
                    (ModelsUnavailable "no Claude account connected")
                    "and what the picker can offer, in the same breath"
            }
    ]

let tests =
    testList "Model catalogue" [
        lookupTests
        catalogueTests
    ]
