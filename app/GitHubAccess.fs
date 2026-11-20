module Yession.Host.GitHubAccess

// What GitHub declares to the credential proxy (`CredentialProxy.fs`), and nothing else: the
// hosts its credential is spent on, the host whose git the git gateway answers, and the
// variables its tools read one from. Beside `GitHubConnection.fs` (signing in) and
// `GitHubPrs.fs` (the first-party pull request tools) for the reason those two are beside
// each other: everything a session knows about GitHub in particular is here, and the proxy
// above it knows only that a provider declared something.
//
// This is the fallback for what the first-party tools do not cover — a workflow run's logs,
// a release, an issue — reached through `gh` or anything else that reads `GH_TOKEN`.

open Yession.Host.CredentialProxy

/// The API, and the host release assets are uploaded to. Not `github.com` itself: a stand-in
/// is swapped on these hosts, and on github.com a stand-in swapped on every path would make it
/// a whole-token credential for the web, archives and LFS. github.com is `gitRoute`.
let route : CredentialRoute =
    { Provider = "github"
      Hosts = [ "api.github.com"; "uploads.github.com" ] }

/// github.com, handled by the git gateway (`GitGateway.fs`): git's three smart-HTTP requests
/// from an admitted sandbox go to the gateway's handler, which adds the credential of the
/// request's loan as its own listener does, and refuses in words. Every other path on
/// github.com goes up with no credential, and without the loan header, which names a loan at
/// the gateway and means nothing upstream. The sandbox is named by its proxy admission, which
/// is what the cap in the gateway's own URL names on the other door.
let gitRoute (gateway: GitGateway.Gateway) : HandledRoute =
    { Host = GitGateway.remoteHost
      Serve = fun sandbox req res -> gateway.Serve (GitGateway.Routed.Sandbox sandbox) req res
      Withheld = [ GitGateway.loanHeader ] }
