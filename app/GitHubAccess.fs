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

open Yession.Domain
open Sandboxes
open Yession.Host
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

/// Which door a sandbox on `backend` reaches the gateway's handler by (`GitGateway.Door`):
/// the credential proxy wherever the proxy admits the backend, and the gateway's own listener,
/// at `listener`, where it does not. srt and seatbelt are never admitted, because their egress
/// is their policy and the proxy's door tunnels any host (`Proxy.Admit`). `None` for a
/// backend with no way to the listener.
let gitDoor (backend: SandboxBackend) (listener: Sandboxes.HostDoor option) : GitGateway.Door option =
    match backend with
    | DockerBackend
    | HostBackend -> Some GitGateway.Door.Proxy
    | SrtBackend
    | SeatbeltBackend -> listener |> Option.map GitGateway.Door.Listener

/// What a sandbox is given for github's `git` route, through its door (`gitDoor`): the route
/// opened at the gateway, and the git config that reaches it. Through the proxy, the sandbox's
/// own proxy URL and the proxy's bundle (mounted into a container), and no `insteadOf`.
/// Through the listener, one `insteadOf` under a fresh cap, and the listener's address for a
/// backend whose egress would otherwise refuse it (srt).
let provideGit
    (gateway: GitGateway.Gateway)
    (proxy: Proxy)
    (backend: SandboxBackend)
    (proxyDoor: Sandboxes.HostDoor option)
    (listener: Sandboxes.HostDoor option)
    (sandbox: SandboxRef)
    : Result<WorkSandboxes.Provision, string> =
    match gitDoor backend listener with
    | None ->
        Error (
            sprintf
                "github cannot be forwarded by git into a %s sandbox: its git would have no route to this session's gateway"
                (SandboxBackend.describe backend)
        )
    | Some GitGateway.Door.Proxy ->
        let https = ProxyValue.Https
        let caFile = ProxyValue.CaFile
        provide proxy backend proxyDoor sandbox [ https; caFile ]
        |> Result.map (fun (values, provision) ->
            gateway.Route (GitGateway.Routed.Sandbox sandbox)
            { provision with GitConfig = GitGateway.proxiedGitConfig (Map.find https values) (Map.find caFile values) })
    | Some (GitGateway.Door.Listener door) ->
        let cap = gateway.Grant (GitGateway.Routed.Sandbox sandbox)
        Ok
            { WorkSandboxes.Provision.empty with
                GitConfig = GitGateway.gitConfig door cap
                // The route's host, for a backend whose egress would otherwise refuse it
                // (srt) — at the gateway's port, by name and by address where srt's proxy
                // re-judges the name.
                Domains = Sandboxes.hostRouteHere (Interop.ownAddresses ()) backend door }
