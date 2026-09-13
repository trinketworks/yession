module Yession.Tests.Requests

// What this host puts on the wire when it asks a provider something, and what it makes of
// the answer.
//
// Every case here was, until recently, a line inside an `[<Emit>]` string: the header a
// credential rides on, the two content types an MCP POST offers, the status that means a
// provider restarted rather than failed, what a rejected `fetch` is reported as. None of it
// was type-checked and none of it was reachable — emit code exists only under Fable, and the
// decisions were nested inside a JS program no test could call.
//
// They are ordinary F# functions now (`app/Http.fs` and the five modules over it), so they
// are asked here, in the cheap tier, with no socket and no provider. The tier is the point:
// a header dialect that a `Ports` suite proves once against a stub server is proved on a run
// almost nobody does, while the rule it encodes changes in an afternoon.
//
// What stays on the wire, and stays under `Ports`, is everything these do not settle: that a
// paged reply arrives whole, that a route is shut to the wrong person, that a real provider
// accepts what is sent. Those are conversations; these are the decisions taken before one.

open Fable.Pyxpecto
open Yession.Host
open Yession.Host.PrWatches

/// One header's value, or `None` when a request does not carry it. Every assertion below
/// reads a request this way rather than comparing whole lists: what matters is that a named
/// header is present with the right value, not what else is beside it or in what order.
let private valueOf (name: string) (request: (string * string) list) : string option =
    request |> List.tryPick (fun (header, value) -> if header = name then Some value else None)

// --- what a request never arriving is reported as -----------------------------------------

let private reasonTests =
    testList "a request that never arrived" [

        testCase "is reported as what the rejection said" <| fun () ->
            Expect.equal (Http.reasonOf (exn "fetch failed")) "fetch failed" "the message is the reason"
    ]

// --- the MCP POST -------------------------------------------------------------------------

let private mcpTests =
    testList "a JSON-RPC POST to a declared MCP server" [

        // Streamable HTTP lets the server pick, so a client that named one would work
        // against half of them.
        testCase "offers both content types the spec lets a server answer with" <| fun () ->
            Expect.equal
                (valueOf "accept" (McpClient.postHeaders "" ""))
                (Some "application/json, text/event-stream")
                "json and an SSE stream, both offered"

        testCase "quotes a session back only once a server has named one" <| fun () ->
            Expect.equal (valueOf "mcp-session-id" (McpClient.postHeaders "" "2025-06-18")) None "nothing to quote yet"
            Expect.equal
                (valueOf "mcp-session-id" (McpClient.postHeaders "sess-1" "2025-06-18"))
                (Some "sess-1")
                "and every request after it carries what the server said"

        testCase "names the protocol version only when there is one to name" <| fun () ->
            Expect.equal (valueOf "mcp-protocol-version" (McpClient.postHeaders "sess-1" "")) None "none declared"
            Expect.equal
                (valueOf "mcp-protocol-version" (McpClient.postHeaders "sess-1" "2025-06-18"))
                (Some "2025-06-18")
                "the version this client speaks"

        testCase "a 2xx is a frame to read" <| fun () ->
            Expect.equal (McpClient.postFailure true 200 "") None "nothing failed"

        // Not a failure to report: the caller a level up re-handshakes on this and retries,
        // which is the only thing that makes a restarted provider recoverable.
        testCase "a 404 is a restarted provider rather than a failure" <| fun () ->
            Expect.equal (McpClient.postFailure true 404 "") (Some "404") "said as the code the caller matches on"

        testCase "any other error status is reported as the status" <| fun () ->
            Expect.equal (McpClient.postFailure true 500 "") (Some "the server answered 500") "what the server said"

        testCase "a request that never arrived is reported as why" <| fun () ->
            Expect.equal (McpClient.postFailure false 0 "connect ECONNREFUSED") (Some "connect ECONNREFUSED") "the reason"
    ]

// --- the Claude model catalogue -------------------------------------------------------------

let private claudeTests =
    testList "a model catalogue lookup" [

        testCase "an api key authenticates with x-api-key" <| fun () ->
            let sent = ClaudeConnection.modelsHeaders "ANTHROPIC_API_KEY" "sk-ant-api03-x"
            Expect.equal (valueOf "x-api-key" sent) (Some "sk-ant-api03-x") "the console dialect"
            Expect.equal (valueOf "authorization" sent) None "and never a bearer beside it"

        testCase "an oauth access token authenticates with a bearer and the beta opt-in" <| fun () ->
            let sent = ClaudeConnection.modelsHeaders "CLAUDE_CODE_OAUTH_TOKEN" "at-1"
            Expect.equal (valueOf "authorization" sent) (Some "Bearer at-1") "the oauth dialect"
            Expect.equal (valueOf "anthropic-beta" sent) (Some "oauth-2025-04-20") "what Claude Code's own client sends"
            Expect.equal (valueOf "x-api-key" sent) None "and never a console key beside it"

        testCase "either dialect declares which api version it speaks" <| fun () ->
            for envVar in [ "ANTHROPIC_API_KEY"; "CLAUDE_CODE_OAUTH_TOKEN" ] do
                Expect.equal
                    (valueOf "anthropic-version" (ClaudeConnection.modelsHeaders envVar "x"))
                    (Some "2023-06-01")
                    "the version this session was written against"
    ]

// --- GitHub ----------------------------------------------------------------------------------

let private githubRequestTests =
    testList "a request to GitHub" [

        // GitHub refuses a request with no user agent outright, and answers an unversioned
        // one with whatever its API means today.
        testCase "says who is asking and which api version it speaks" <| fun () ->
            let sent = GitHubPrs.sentHeaders ""
            Expect.equal (valueOf "user-agent" sent) (Some "yession") "GitHub refuses a request without one"
            Expect.equal (valueOf "accept" sent) (Some "application/vnd.github+json") "the versioned media type"
            Expect.equal (valueOf "x-github-api-version" sent) (Some "2022-11-28") "the API this side was written against"

        testCase "carries a bearer only when there is a credential" <| fun () ->
            Expect.equal (valueOf "authorization" (GitHubPrs.sentHeaders "")) None "anonymous, which GitHub still answers"
            Expect.equal (valueOf "authorization" (GitHubPrs.sentHeaders "ghp_x")) (Some "Bearer ghp_x") "on this credential"

        // A first look has no ETag, and `if-none-match: ` is a condition on the empty string
        // rather than an absent condition.
        testCase "quotes an etag back only when the caller has one" <| fun () ->
            Expect.equal (valueOf "if-none-match" (GitHubPrs.conditionalHeaders "ghp_x" "")) None "a first look"
            Expect.equal
                (valueOf "if-none-match" (GitHubPrs.conditionalHeaders "ghp_x" "W/\"abc\""))
                (Some "W/\"abc\"")
                "so an unchanged resource costs a 304 rather than a body"

        // `GitHubPrs` and `GitHubRepos` each own what they know about the provider, so these
        // three headers are written twice on purpose. This is what says the two copies are
        // one decision: change how this session presents itself, and change it in both.
        testCase "presents itself identically whichever endpoint family is asking" <| fun () ->
            Expect.equal (GitHubRepos.sentHeaders "ghp_x") (GitHubPrs.sentHeaders "ghp_x") "one session, one introduction"

        // GitHub's OAuth endpoints answer form-encoded unless asked for JSON, and the
        // device flow's whole reply — the code pair, the interval, the grant — is read as
        // JSON, so this header is what makes any of it decodable.
        testCase "the device flow asks github's oauth endpoints for json" <| fun () ->
            Expect.equal (valueOf "accept" GitHubConnection.deviceFlowHeaders) (Some "application/json") "asked for"
            Expect.equal (valueOf "content-type" GitHubConnection.deviceFlowHeaders) (Some "application/json") "and sent"

        // The credential IS the question these two endpoints ask, so there is nothing to
        // send them without one.
        testCase "the credential check always carries the bearer" <| fun () ->
            Expect.equal
                (valueOf "authorization" (GitHubConnection.userHeaders "ghp_x"))
                (Some "Bearer ghp_x")
                "asking GitHub whether it still accepts this"
    ]

let private githubStatusTests =
    testList "what a status GitHub answered with means" [

        testCase "a 401 is the credential, for a look and a listing alike" <| fun () ->
            Expect.equal (GitHubPrs.failureAt 401 "") PrUnauthorized "the pull request endpoints"
            Expect.equal (GitHubRepos.failureAt 401) GitHubRepos.Refused "and the repository ones"

        testCase "a 404 is something this credential cannot see" <| fun () ->
            Expect.equal (GitHubPrs.failureAt 404 "") PrNotFound "GitHub says this for absent and for hidden alike"
            Expect.equal (GitHubRepos.failureAt 404) GitHubRepos.NotFound "the same on a listing"

        // 403 is how GitHub says "too many"; a 403 for any other reason (scopes, a blocked
        // App) is also not something a retry sooner would fix.
        testCase "403 and 429 are both the allowance being spent" <| fun () ->
            Expect.equal (GitHubPrs.failureAt 403 "") (PrRateLimited None) "a 403 waits"
            Expect.equal (GitHubPrs.failureAt 429 "") (PrRateLimited None) "and so does a 429"
            Expect.equal (GitHubRepos.failureAt 403) GitHubRepos.RateLimited "the same on a listing"
            Expect.equal (GitHubRepos.failureAt 429) GitHubRepos.RateLimited "and the same again"

        testCase "a rate limit carries the window it ends at, when GitHub named one" <| fun () ->
            Expect.equal (GitHubPrs.failureAt 429 "1770000000") (PrRateLimited (Some 1770000000)) "the reset epoch"

        testCase "any other status is reported as what GitHub answered" <| fun () ->
            Expect.equal (GitHubPrs.failureAt 502 "") (PrUnreachable "github answered 502") "a gateway between us"
            Expect.equal (GitHubRepos.failureAt 502) (GitHubRepos.Unreachable "github answered 502") "the same on a listing"
    ]

let tests =
    testList "Requests" [ reasonTests; mcpTests; claudeTests; githubRequestTests; githubStatusTests ]
