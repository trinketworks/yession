module Yession.Host.GitHubAccess

// What GitHub declares to the credential proxy (`CredentialProxy.fs`), and nothing else: the
// hosts its credential is spent on, and the variables its tools read one from. Beside
// `GitHubConnection.fs` (signing in) and `GitHubPrs.fs` (the first-party pull request tools)
// for the reason those two are beside each other: everything a session knows about GitHub in
// particular is here, and the proxy above it knows only that a provider declared something.
//
// This is the fallback for what the first-party tools do not cover — a workflow run's logs,
// a release, an issue — reached through `gh` or anything else that reads `GH_TOKEN`.

open Yession.Host.CredentialProxy

/// The API, and the host release assets are uploaded to. Not `github.com` itself: git's own
/// traffic still goes through the git gateway (`GitGateway.fs`), and a host declared here is a
/// host whose TLS the proxy terminates.
let route : CredentialRoute =
    { Provider = "github"
      Hosts = [ "api.github.com"; "uploads.github.com" ]
      // `gh` reads the first; most everything else in the GitHub ecosystem, the second.
      Variables = [ "GH_TOKEN"; "GITHUB_TOKEN" ] }
